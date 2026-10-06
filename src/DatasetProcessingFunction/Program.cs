using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Monitor.OpenTelemetry.Exporter;
using DatasetProcessingFunction.Application.Commands;
using DatasetProcessingFunction.Application.Interfaces;
using DatasetProcessingFunction.Domain.Services;
using DatasetProcessingFunction.Domain.Telemetry;
using DatasetProcessingFunction.Infrastructure.Contracts;
using DatasetProcessingFunction.Infrastructure.Messaging;
using DatasetProcessingFunction.Infrastructure.Telemetry;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using DatasetProcessingFunction.Infrastructure.Configuration;

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

// Data-account storage (dataset reads + Bronze writes, ADR 0005) and schema-registry storage
// (a separate storage account) — settings resolution, client construction, resilience pipelines,
// and interface bindings all live in these two extension methods. Each validates its own
// configuration via the options pattern with ValidateOnStart(): a missing/invalid setting throws
// during host start (IHost.StartAsync), not lazily on the first message or warmup trigger.
builder.Services.AddDataStorage(builder.Configuration);
builder.Services.AddSchemaRegistry(builder.Configuration);

builder.Services.AddScoped<IEventPublisher, ServiceBusEventPublisher>();
// R3.7: validates inbound solar.pvdaq.dataset.available.v1 envelopes against the vendored
// contract before ProcessDatasetFunction deserialises them. Stateless (the compiled schema is a
// static field) — singleton is just to avoid re-resolving it per message.
builder.Services.AddSingleton<IDatasetEventValidator, DatasetAvailableEventValidator>();
builder.Services.AddSingleton<IProcessingMetricsEmitter, ProcessingMetricsEmitter>();

// Domain services
builder.Services.AddSingleton<CsvParserService>();
builder.Services.AddSingleton<SchemaTransformer>();
builder.Services.AddSingleton<DataQualityValidator>();
builder.Services.AddSingleton<RecordEnricher>();

builder.Build().Run();
