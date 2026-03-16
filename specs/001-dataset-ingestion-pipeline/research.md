# Research: Dataset Ingestion Pipeline

**Branch**: `001-dataset-ingestion-pipeline` | **Date**: 2026-03-15
**Phase**: 0 — Pre-design research

---

## 1. Parquet Writer for Bronze Layer

**Decision**: `Parquet.Net` (NuGet: `Parquet.Net`, v4.x)

**Rationale**: Pure-managed .NET library with zero native dependencies — critical for reliable
deployment on Azure Functions Premium plan without custom runtime configuration. At ≤10,000
rows per invocation (FR-008) memory usage is well within Premium plan limits (~1.5 GB RAM).
The 4.x API supports columnar writes and schema-first construction which maps cleanly to
`CanonicalRecord`.

**Alternatives considered**:
- `ParquetSharp` (v13.x): Wraps Apache Arrow C++, true streaming writes, best for large files.
  Rejected — requires native C++ runtime bundled with the deployment package, complicating CI/CD
  and Bicep provisioning. Revisit if row counts exceed 100K.
- `Apache.Arrow` directly: Low-level, no built-in Parquet serialisation. Rejected — high effort.

**Package**: `Parquet.Net` ≥ 4.23.0

---

## 2. Bronze Layer Write Endpoint (OneLake / ADLS Gen2)

**Decision**: `Azure.Storage.Files.DataLake` SDK (`DataLakeServiceClient`) targeting the
OneLake ADLS-compatible DFS endpoint.

**Endpoint format**:
```
https://onelake.dfs.fabric.microsoft.com/{workspace-id}/{lakehouse-id}
```

**Bronze partition path pattern**:
```
Files/bronze/{dataset_id}/{ingestion_date}/data.parquet
```

**Idempotent overwrite**: Delete the partition directory then re-upload (atomic replace of the
entire `{dataset_id}/{ingestion_date}/` directory satisfies FR-018).

**Auth**: `DefaultAzureCredential` — works transparently with System-Assigned Managed Identity
in Azure and local developer credentials via `az login`.

**Package**: `Azure.Storage.Files.DataLake` ≥ 12.18.0

---

## 3. Service Bus Publisher for Downstream Events

**Decision**: `ServiceBusSender` registered as **Singleton** in DI, created via
`ServiceBusClient.CreateSender(topicName)`.

**Rationale**: Output binding (`[ServiceBusOutput]`) is simpler but insufficient here — we need
full control over message properties (`CorrelationId`, `MessageId` for deduplication,
`ContentType: application/json`) and conditional publishing (FR-026: event must NOT be sent if
Bronze write fails). Direct `ServiceBusSender` singleton satisfies all requirements.

**Registration pattern** (Program.cs):
```csharp
builder.Services.AddSingleton(sp =>
    new ServiceBusClient(
        new Uri($"sb://{namespace}.servicebus.windows.net"),
        new DefaultAzureCredential()));

builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<ServiceBusClient>()
      .CreateSender("dataset-events"));
```

**Packages**:
- `Azure.Messaging.ServiceBus` ≥ 7.18.0
- `Microsoft.Azure.Functions.Worker.Extensions.ServiceBus` ≥ 5.16.0 (trigger binding)

---

## 4. MediatR / CQRS Registration

**Decision**: MediatR 12.x registered via `AddMediatR` scanning the Application assembly.

**Program.cs pattern**:
```csharp
builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssemblyContaining<ProcessDatasetCommand>());
```

**Command flow**:
```
Function trigger
  └─ deserialise DatasetAvailableEvent
  └─ IMediator.Send(new ProcessDatasetCommand(...))
       └─ ProcessDatasetCommandHandler
            ├─ RetrieveDatasetQuery → Infrastructure (ADLS read)
            ├─ ParseCsvCommand → Domain (pure)
            ├─ ValidateDatasetCommand → Domain (pure)
            ├─ TransformRecordsCommand → Domain (pure)
            ├─ WriteTooBronzeCommand → Infrastructure (ADLS write)
            └─ IMediator.Publish(DatasetBronzeAvailableNotification)
                  └─ DatasetBronzePublisher → Infrastructure (Service Bus send)
```

Publishing the downstream event via MediatR `INotification` keeps the command handler
focused and makes Service Bus publishing independently testable.

**Package**: `MediatR` ≥ 12.2.0

---

## 5. OpenTelemetry Wiring

**Decision**: `Microsoft.Azure.Functions.Worker.OpenTelemetry` +
`Azure.Monitor.OpenTelemetry.Exporter` via `UseFunctionsWorkerDefaults()`.

**Program.cs pattern**:
```csharp
builder.Services
    .AddOpenTelemetry()
    .UseFunctionsWorkerDefaults()
    .UseAzureMonitorExporter();

builder.Logging.AddOpenTelemetry(b => b.IncludeScopes = true);
```

**host.json**:
```json
{ "version": "2.0", "telemetryMode": "OpenTelemetry" }
```

**Custom ActivitySource** for domain span instrumentation:
```csharp
internal static readonly ActivitySource Source =
    new("DatasetProcessingFunction", "1.0.0");
```

Span names follow `dataset.{operation}` convention per constitution Principle IX.

**Packages**:
- `Microsoft.Azure.Functions.Worker.OpenTelemetry` ≥ 1.1.0
- `OpenTelemetry.Extensions.Hosting` ≥ 1.10.0
- `Azure.Monitor.OpenTelemetry.Exporter` ≥ 1.3.0

---

## 6. CSV Parsing

**Decision**: `CsvHelper` (NuGet: `CsvHelper`) for robust delimiter-configurable CSV parsing.

**Rationale**: Native `StreamReader` + `string.Split` cannot handle quoted fields containing
the delimiter character. `CsvHelper` is the de-facto standard .NET CSV library, supports
configurable delimiters, handles mixed line endings transparently (FR-006), and is allocation-
efficient via streaming `IAsyncEnumerable<T>` reads.

**Encoding check**: Read the BOM / encoding via `StreamReader` before passing to CsvHelper;
reject if not UTF-8 (FR-006b).

**Package**: `CsvHelper` ≥ 33.0.0

---

## 7. Testing Stack

| Layer | Package | Purpose |
|-------|---------|---------|
| Unit runner | `xunit` ≥ 2.9.0 | Domain + Application tests |
| Mocking | `Moq` ≥ 4.20.0 | Infrastructure interfaces |
| Coverage | `coverlet.collector` ≥ 6.0.0 | ≥ 80% gate |
| ADLS emulator | `Azurite` (npm / Docker) | Integration tests for ADLS reads/writes |
| Service Bus emulator | `Azure Service Bus emulator` (Docker) | Integration tests for trigger + publish |
| Assertions | `FluentAssertions` ≥ 6.12.0 | Readable test assertions |

---

## 8. Full NuGet Package List

| Package | Min Version | Layer |
|---------|-------------|-------|
| `Microsoft.Azure.Functions.Worker` | 2.0.0 | Host |
| `Microsoft.Azure.Functions.Worker.Sdk` | 2.0.5 | Host |
| `Microsoft.Azure.Functions.Worker.Extensions.ServiceBus` | 5.16.0 | Host |
| `Microsoft.Azure.Functions.Worker.OpenTelemetry` | 1.1.0 | Host |
| `OpenTelemetry.Extensions.Hosting` | 1.10.0 | Host |
| `Azure.Monitor.OpenTelemetry.Exporter` | 1.3.0 | Host |
| `Azure.Identity` | 1.13.0 | Host + Infra |
| `MediatR` | 12.2.0 | Application |
| `FluentValidation` | 11.9.0 | Application |
| `Azure.Messaging.ServiceBus` | 7.18.0 | Infrastructure |
| `Azure.Storage.Files.DataLake` | 12.18.0 | Infrastructure |
| `Parquet.Net` | 4.23.0 | Infrastructure |
| `CsvHelper` | 33.0.0 | Infrastructure |
| `Microsoft.Extensions.Configuration.AzureAppConfiguration` | 8.0.0 | Infrastructure (schema registry) |
