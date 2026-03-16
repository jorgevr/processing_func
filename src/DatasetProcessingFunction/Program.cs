using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Azure.Storage.Files.DataLake;
using DatasetProcessingFunction.Application.Commands;
using DatasetProcessingFunction.Application.Interfaces;
using DatasetProcessingFunction.Infrastructure.Messaging;
using DatasetProcessingFunction.Infrastructure.SchemaRegistry;
using DatasetProcessingFunction.Infrastructure.Storage;
using DatasetProcessingFunction.Infrastructure.Telemetry;
using DatasetProcessingFunction.Domain.Telemetry;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// OpenTelemetry — per constitution Principle IX
// Do NOT use AddApplicationInsightsTelemetryWorkerService() alongside OTel path
// Use Azure Monitor distro first (returns OpenTelemetryBuilder), then configure Functions defaults
var otelBuilder = builder.Services
    .AddOpenTelemetry()
    .UseAzureMonitor()
    .WithTracing(tracing =>
    {
        tracing.AddSource(DatasetActivitySource.Source.Name);
    })
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(ProcessingMetricsEmitter.MeterName);
    });

// Configure Azure Functions-specific OTel defaults (resource detection, dedup)
otelBuilder.UseFunctionsWorkerDefaults();

// IncludeScopes is configured via OpenTelemetryLoggerOptions — covered by UseAzureMonitor distro
// If needed explicitly: builder.Services.Configure<OpenTelemetryLoggerOptions>(o => o.IncludeScopes = true);

// MediatR
builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssemblyContaining<ProcessDatasetCommand>());

// Azure clients — Managed Identity (DefaultAzureCredential)
var credential = new DefaultAzureCredential();

var serviceBusNamespace = builder.Configuration["ServiceBusConnection__fullyQualifiedNamespace"]
    ?? throw new InvalidOperationException("ServiceBusConnection__fullyQualifiedNamespace is required.");

builder.Services.AddSingleton(new ServiceBusClient(serviceBusNamespace, credential));

builder.Services.AddSingleton(sp =>
{
    var sbClient = sp.GetRequiredService<ServiceBusClient>();
    var topicName = builder.Configuration["SERVICEBUS_TOPIC_NAME"] ?? "dataset-events";
    return sbClient.CreateSender(topicName);
});

builder.Services.AddSingleton(sp =>
{
    var adlsEndpoint = builder.Configuration["ADLS_ENDPOINT"]
        ?? throw new InvalidOperationException("ADLS_ENDPOINT is required.");
    return new DataLakeServiceClient(new Uri(adlsEndpoint), credential);
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

// Interface bindings
builder.Services.AddScoped<IDatasetReader, AdlsDatasetReader>();
builder.Services.AddScoped<IBronzeWriter>(sp =>
{
    var dlClient = sp.GetRequiredService<DataLakeServiceClient>();
    var filesystem = builder.Configuration["BRONZE_FILESYSTEM"] ?? "bronze";
    var onelakeEndpoint = builder.Configuration["ONELAKE_ENDPOINT"];
    return new OneLakeBronzeWriter(dlClient, filesystem, onelakeEndpoint, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OneLakeBronzeWriter>>());
});
builder.Services.AddSingleton<ISchemaRegistry>(sp =>
{
    var blobClient = sp.GetRequiredService<BlobServiceClient>();
    var container = builder.Configuration["SCHEMA_REGISTRY_CONTAINER"] ?? "schema-registry";
    return new BlobSchemaRegistry(blobClient, container, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BlobSchemaRegistry>>());
});
builder.Services.AddScoped<IEventPublisher, ServiceBusEventPublisher>();
builder.Services.AddSingleton<ProcessingMetricsEmitter>();

builder.Build().Run();
