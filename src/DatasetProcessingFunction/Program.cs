using System.IO;
using Azure;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Monitor.OpenTelemetry.Exporter;
using Azure.Storage.Blobs;
using DatasetProcessingFunction.Application.Commands;
using DatasetProcessingFunction.Application.Interfaces;
using DatasetProcessingFunction.Domain.Services;
using DatasetProcessingFunction.Domain.Telemetry;
using DatasetProcessingFunction.Infrastructure.Messaging;
using DatasetProcessingFunction.Infrastructure.SchemaRegistry;
using DatasetProcessingFunction.Infrastructure.Storage;
using DatasetProcessingFunction.Infrastructure.Telemetry;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Resilience;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Polly;
using Polly.Registry;
using Polly.Retry;

var builder = FunctionsApplication.CreateBuilder(args);

// Remove the default App Insights Warning+ filter so all log levels reach Application Insights.
// Target only the specific built-in rule rather than clearing all rules (Constitution VIII).
builder.Services.Configure<LoggerFilterOptions>(opts =>
{
    var rule = opts.Rules.FirstOrDefault(r =>
        r.ProviderName is not null &&
        r.ProviderName.Contains("ApplicationInsights", StringComparison.OrdinalIgnoreCase) &&
        r.LogLevel >= LogLevel.Warning);
    if (rule is not null)
        opts.Rules.Remove(rule);
});

// OpenTelemetry — Exporter-only path (no AspNetCore distro, no duplicate request spans — Constitution IX)
var appInsightsConnStr = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddSource(DatasetActivitySource.Source.Name);
        if (!string.IsNullOrWhiteSpace(appInsightsConnStr))
            tracing.AddAzureMonitorTraceExporter(o => o.ConnectionString = appInsightsConnStr);
    })
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(ProcessingMetricsEmitter.MeterName);
        if (!string.IsNullOrWhiteSpace(appInsightsConnStr))
            metrics.AddAzureMonitorMetricExporter(o => o.ConnectionString = appInsightsConnStr);
    })
    .UseFunctionsWorkerDefaults();

// MediatR
builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssemblyContaining<ProcessDatasetCommand>());

// Azure clients — production uses Managed Identity; emulator uses SAS connection string
var credential = new DefaultAzureCredential();

// Dual-mode ServiceBusClient: SAS for local emulator, Managed Identity for production (research.md §2)
var sbConnStr = builder.Configuration["ServiceBusConnection"];
ServiceBusClient sbClient;
if (!string.IsNullOrWhiteSpace(sbConnStr) &&
    sbConnStr.Contains("UseDevelopmentEmulator", StringComparison.OrdinalIgnoreCase))
{
    sbClient = new ServiceBusClient(sbConnStr);
}
else
{
    var ns = builder.Configuration["SERVICEBUS_NAMESPACE"]
        ?? throw new InvalidOperationException("SERVICEBUS_NAMESPACE is required.");
    sbClient = new ServiceBusClient(ns, credential);
}
builder.Services.AddSingleton(sbClient);

// Queue sender for downstream Bronze-available events (FR-025, Basic tier = queues only)
builder.Services.AddSingleton(sp =>
{
    var queueName = builder.Configuration["SERVICEBUS_BRONZE_QUEUE_NAME"]
        ?? throw new InvalidOperationException("SERVICEBUS_BRONZE_QUEUE_NAME is required.");
    return sp.GetRequiredService<ServiceBusClient>().CreateSender(queueName);
});

// Data-account BlobServiceClient — dataset reads + Bronze writes (ADR 0005). Distinct from the
// schema registry account below: the two are separate storage accounts (docs/contracts.md "Shared
// configuration"). Retry disabled (MaxRetries=0) so Polly owns all retry logic (research.md §3).
// Dual-mode: connection string for local emulator (Azurite has no OAuth) wins if set, else an
// account URL + Managed Identity for production.
builder.Services.AddKeyedSingleton(DataStorageKeys.DataStorage, (sp, _) =>
{
    var options = new BlobClientOptions { Retry = { MaxRetries = 0 } };
    var dataConnStr = builder.Configuration["DATA_STORAGE_CONNECTION"];
    if (!string.IsNullOrWhiteSpace(dataConnStr))
        return new BlobServiceClient(dataConnStr, options);
    var dataAccountUrl = builder.Configuration["DATA_STORAGE_ACCOUNT_URL"];
    if (!string.IsNullOrWhiteSpace(dataAccountUrl))
        return new BlobServiceClient(new Uri(dataAccountUrl), credential, options);
    throw new InvalidOperationException(
        "Either DATA_STORAGE_CONNECTION or DATA_STORAGE_ACCOUNT_URL is required.");
});

// Schema registry BlobServiceClient — a separate storage account from the data account above.
// Retry disabled (MaxRetries=0) so Polly owns all retry logic (research.md §3). Dual-mode:
// connection string for local emulator (Azurite has no OAuth), Managed Identity for production.
builder.Services.AddSingleton(sp =>
{
    var options = new BlobClientOptions { Retry = { MaxRetries = 0 } };
    var schemaConnStr = builder.Configuration["SCHEMA_REGISTRY_BLOB_CONNECTION"];
    if (!string.IsNullOrWhiteSpace(schemaConnStr))
        return new BlobServiceClient(schemaConnStr, options);
    return new BlobServiceClient(
        new Uri($"https://{builder.Configuration["SCHEMA_REGISTRY_ACCOUNT"]}.blob.core.windows.net"),
        credential, options);
});

// Polly v8 resilience pipeline for ADLS transient fault retry (research.md §3)
// MaxRetryAttempts=2 = 3 total attempts (1 initial + 2 retries); Azure SDK MaxRetries=0 avoids multiplicative retries
builder.Services.AddResiliencePipeline("adls-read", pipeline =>
    pipeline.AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts = 2,
        Delay = TimeSpan.FromSeconds(2),
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        MaxDelay = TimeSpan.FromSeconds(30),
        ShouldHandle = new PredicateBuilder()
            .Handle<IOException>()
            .Handle<RequestFailedException>(ex =>
                ex.Status is 429 or 500 or 503 or 408)
    }));

// Polly v8 resilience pipeline for Schema Registry transient fault retry (research.md §4)
// 404 (blob not found = unknown schema) is NOT in ShouldHandle — propagates immediately as UnknownSchemaException
builder.Services.AddResiliencePipeline("schema-registry-read", pipeline =>
    pipeline.AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts = 2,
        Delay = TimeSpan.FromSeconds(2),
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        MaxDelay = TimeSpan.FromSeconds(30),
        ShouldHandle = new PredicateBuilder()
            .Handle<IOException>()
            .Handle<RequestFailedException>(ex =>
                ex.Status is 429 or 500 or 503 or 408)
    }));

// Interface bindings — dataset reads and Bronze writes use the data-account client (not the
// schema registry client above).
builder.Services.AddScoped<IDatasetReader>(sp =>
{
    var blobClient = sp.GetRequiredKeyedService<BlobServiceClient>(DataStorageKeys.DataStorage);
    var pipelineProvider = sp.GetRequiredService<ResiliencePipelineProvider<string>>();
    return new AdlsDatasetReader(blobClient, pipelineProvider,
        sp.GetRequiredService<ILogger<AdlsDatasetReader>>());
});
builder.Services.AddScoped<IBronzeWriter>(sp =>
{
    var blobClient = sp.GetRequiredKeyedService<BlobServiceClient>(DataStorageKeys.DataStorage);
    var container = builder.Configuration["BRONZE_CONTAINER"] ?? "bronze";
    return new BronzeWriter(blobClient, container,
        sp.GetRequiredService<ILogger<BronzeWriter>>());
});
builder.Services.AddSingleton<ISchemaRegistry>(sp =>
{
    var blobClient = sp.GetRequiredService<BlobServiceClient>();
    var container = builder.Configuration["SCHEMA_REGISTRY_CONTAINER"] ?? "schema-registry";
    var pipelineProvider = sp.GetRequiredService<ResiliencePipelineProvider<string>>();
    return new BlobSchemaRegistry(blobClient, container, pipelineProvider,
        sp.GetRequiredService<ILogger<BlobSchemaRegistry>>());
});
builder.Services.AddScoped<IEventPublisher, ServiceBusEventPublisher>();
builder.Services.AddSingleton<IProcessingMetricsEmitter, ProcessingMetricsEmitter>();

// Domain services
builder.Services.AddSingleton<CsvParserService>();
builder.Services.AddSingleton<SchemaTransformer>();
builder.Services.AddSingleton<DataQualityValidator>();
builder.Services.AddSingleton<RecordEnricher>();

builder.Build().Run();
