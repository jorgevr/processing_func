using System.IO;
using Azure;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Monitor.OpenTelemetry.Exporter;
using Azure.Storage.Blobs;
using Azure.Storage.Files.DataLake;
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

// DataLakeServiceClient — disable SDK built-in retry (MaxRetries=0) so Polly owns all retry logic (research.md §3)
// Dual-mode: shared-key connection string for local emulator (Azurite has no OAuth), Managed Identity for production
builder.Services.AddSingleton(sp =>
{
    var adlsEndpoint = builder.Configuration["ADLS_ENDPOINT"]
        ?? throw new InvalidOperationException("ADLS_ENDPOINT is required.");
    var options = new DataLakeClientOptions { Retry = { MaxRetries = 0 } };
    var adlsConnStr = builder.Configuration["ADLS_CONNECTION_STRING"];
    if (!string.IsNullOrWhiteSpace(adlsConnStr))
        return new DataLakeServiceClient(adlsConnStr, options);
    return new DataLakeServiceClient(new Uri(adlsEndpoint), credential, options);
});

builder.Services.AddSingleton(sp =>
{
    var schemaConnStr = builder.Configuration["SCHEMA_REGISTRY_BLOB_CONNECTION"];
    if (!string.IsNullOrWhiteSpace(schemaConnStr) && schemaConnStr == "UseDevelopmentStorage=true")
        return new BlobServiceClient(schemaConnStr);
    return new BlobServiceClient(
        new Uri($"https://{builder.Configuration["SCHEMA_REGISTRY_ACCOUNT"]}.blob.core.windows.net"),
        credential);
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

// Interface bindings
builder.Services.AddScoped<IDatasetReader, AdlsDatasetReader>();
builder.Services.AddScoped<IBronzeWriter>(sp =>
{
    var dlClient = sp.GetRequiredService<DataLakeServiceClient>();
    var filesystem = builder.Configuration["BRONZE_FILESYSTEM"] ?? "bronze";
    var onelakeEndpoint = builder.Configuration["ONELAKE_ENDPOINT"];
    return new OneLakeBronzeWriter(dlClient, filesystem, onelakeEndpoint,
        sp.GetRequiredService<ILogger<OneLakeBronzeWriter>>());
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
