# Implementation Plan: Dataset Ingestion Pipeline

**Branch**: `001-dataset-ingestion-pipeline` | **Date**: 2026-03-15 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/001-dataset-ingestion-pipeline/spec.md`

---

## Summary

A .NET 10 isolated-worker Azure Function that consumes `dataset.available` events from
Service Bus, retrieves vendor CSV files from ADLS Gen2, parses/validates/transforms them
into canonical `CanonicalRecord` instances, writes Parquet files to the Fabric Lakehouse
Bronze layer via the OneLake ADLS-compatible endpoint, and publishes a
`dataset.bronze.available` event downstream. All processing is idempotent by `dataset_id`,
all-or-nothing on validation failure, and fully instrumented with OpenTelemetry.

---

## Technical Context

**Language/Version**: .NET 10 (LTS) Isolated Worker
**Primary Dependencies**: MediatR 12.x, CsvHelper 33.x, Parquet.Net 4.23.x, Azure.Storage.Files.DataLake 12.18.x, Azure.Messaging.ServiceBus 7.18.x, OpenTelemetry 1.10.x
**Storage**: ADLS Gen2 (input CSV) + OneLake ADLS-compatible endpoint (Bronze Parquet output) + Azure Blob (schema registry)
**Testing**: xUnit 2.9.x + Moq 4.20.x + FluentAssertions 6.12.x + Azurite + Service Bus emulator
**Target Platform**: Azure Functions v4, Premium plan (EP1+), Windows or Linux
**Project Type**: Event-driven serverless function (isolated worker)
**Performance Goals**: ≤ 60s end-to-end for 10,000-row dataset (SC-001); 50 concurrent events without loss (SC-005)
**Constraints**: All-or-nothing Bronze write; idempotent by `dataset_id`; UTF-8 only; Managed Identity auth throughout
**Scale/Scope**: Single bounded context; 4 .NET projects + 2 test projects; ≤ 30 Bicep module lines per resource
**OpenTelemetry Exporter**: Azure Monitor (`Azure.Monitor.OpenTelemetry.Exporter`) — OTLP endpoint optional
**CI/CD Platform**: GitHub Actions (or Azure DevOps); `AzureFunctionApp@2`; Workload Identity Federation (OIDC)

---

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked post-design.*

| Principle | Gate | Status |
| --------- | ---- | ------ |
| **I — EDA First** | Triggered exclusively by Service Bus; emits `dataset.bronze.available` downstream; DLQ configured | ✅ Pass |
| **II — DDD** | `DatasetProcessingJob` aggregate; `DatasetId`, `CanonicalRecord`, `ValidationResult` value objects; ubiquitous language throughout | ✅ Pass |
| **III — CQRS** | `ProcessDatasetCommand` + handler; no handler reads and mutates in the same operation; MediatR dispatch; `INotification` for downstream publish | ✅ Pass |
| **IV — IaC Bicep** | All resources (Function App, Service Bus, Storage, App Insights, Key Vault) in `infrastructure/modules/`; `azure.yaml` entry point | ✅ Pass |
| **V — Observability** | `telemetryMode: OpenTelemetry` in host.json; W3C traceparent propagated; `OTEL_SERVICE_NAME` in Bicep params; alerts on DLQ depth | ✅ Pass |
| **VI — Security** | Managed Identity everywhere; no connection strings; Key Vault for secrets; RBAC in Bicep | ✅ Pass |
| **VII — Test-First** | xUnit unit tests for Domain + Application (≥ 80% coverage gate); integration tests against Azurite + Service Bus emulator | ✅ Pass |
| **VIII — Isolated Worker** | `FunctionsApplication.CreateBuilder`; `ServiceBusSender` Singleton; `async Task` signatures; `CancellationToken` propagated; Run from Package | ✅ Pass |
| **IX — OpenTelemetry** | `Microsoft.Azure.Functions.Worker.OpenTelemetry`; `.UseFunctionsWorkerDefaults().UseAzureMonitorExporter()`; `IncludeScopes = true`; custom ActivitySource; no ConsoleExporter in prod | ✅ Pass |
| **Workflow §8 — CI/CD** | OIDC Workload Identity; Build → Unit (≥80%) → Integration → Bicep What-If → Staging Slot → Health Check → Swap stages; `AzureFunctionApp@2` | ✅ Pass |

**No constitution violations. Complexity Tracking table empty.**

---

## Project Structure

### Documentation (this feature)

```text
specs/001-dataset-ingestion-pipeline/
├── plan.md          ← this file
├── research.md      ← Phase 0 output
├── data-model.md    ← Phase 1 output
├── quickstart.md    ← Phase 1 output
├── contracts/
│   └── events/
│       ├── dataset-available.json
│       └── dataset-bronze-available.json
└── tasks.md         ← Phase 2 output (/speckit.tasks)
```

### Source Code (repository root)

```text
DatasetProcessingFunction.sln

src/
├── DatasetProcessingFunction/                       # Azure Functions host project
│   ├── Functions/
│   │   └── ProcessDatasetFunction.cs                # Thin trigger: deserialise → IMediator.Send
│   ├── Program.cs                                   # IHostApplicationBuilder, OTel, DI wiring
│   ├── host.json                                    # telemetryMode: OpenTelemetry, SB settings
│   └── local.settings.json.template
│
├── DatasetProcessingFunction.Application/           # CQRS layer
│   ├── Commands/
│   │   ├── ProcessDatasetCommand.cs
│   │   ├── ProcessDatasetCommandHandler.cs
│   │   └── ProcessDatasetResult.cs
│   ├── Notifications/
│   │   ├── DatasetBronzeAvailableNotification.cs
│   │   └── DatasetBronzeAvailablePublisher.cs       # Infrastructure adapter (Service Bus send)
│   └── Interfaces/
│       ├── IDatasetReader.cs                        # ADLS CSV retrieval
│       ├── IBronzeWriter.cs                         # Parquet write to OneLake
│       ├── ISchemaRegistry.cs                       # VendorSchemaMapping lookup
│       └── IEventPublisher.cs                       # Service Bus message send
│
├── DatasetProcessingFunction.Domain/                # Pure domain, no infrastructure deps
│   ├── Aggregates/
│   │   └── DatasetProcessingJob.cs
│   ├── ValueObjects/
│   │   ├── DatasetId.cs
│   │   ├── RawRecord.cs
│   │   ├── CanonicalRecord.cs
│   │   ├── ValidationResult.cs
│   │   ├── ValidationFailure.cs
│   │   └── ProcessingMetrics.cs
│   ├── Services/
│   │   ├── CsvParserService.cs                      # Pure parse + encoding check
│   │   ├── DataQualityValidator.cs                  # FR-009, FR-010, FR-011
│   │   ├── SchemaTransformer.cs                     # FR-013–FR-015
│   │   └── RecordEnricher.cs                        # FR-016
│   └── Enums/
│       └── ProcessingStatus.cs
│
└── DatasetProcessingFunction.Infrastructure/        # Adapters for external services
    ├── Storage/
    │   ├── AdlsDatasetReader.cs                     # IDatasetReader → DataLakeServiceClient
    │   └── OneLakeBronzeWriter.cs                   # IBronzeWriter → DataLakeServiceClient + Parquet.Net
    ├── Messaging/
    │   └── ServiceBusEventPublisher.cs              # IEventPublisher → ServiceBusSender
    ├── SchemaRegistry/
    │   └── BlobSchemaRegistry.cs                    # ISchemaRegistry → Azure Blob JSON config
    └── Telemetry/
        └── DatasetActivitySource.cs                 # ActivitySource("DatasetProcessingFunction")

tests/
├── DatasetProcessingFunction.UnitTests/
│   ├── Domain/
│   │   ├── CsvParserServiceTests.cs
│   │   ├── DataQualityValidatorTests.cs
│   │   ├── SchemaTransformerTests.cs
│   │   └── RecordEnricherTests.cs
│   └── Application/
│       └── ProcessDatasetCommandHandlerTests.cs
│
└── DatasetProcessingFunction.IntegrationTests/
    ├── ProcessDatasetEndToEndTests.cs               # Azurite + SB emulator
    └── fixtures/
        ├── vendor-abc-v2-valid.csv
        ├── vendor-abc-v2-missing-timestamp.csv
        ├── vendor-abc-v2-invalid-range.csv
        └── schema-registry/
            └── vendor-abc-v2.json

infrastructure/
├── azure.yaml                                       # AZD entry point
├── main.bicep
├── main.parameters.json
└── modules/
    ├── function-app.bicep                           # Premium plan + slots + Run from Package
    ├── service-bus.bicep                            # Namespace, queues (Basic tier — no topics/subscriptions), DLQ
    ├── storage.bicep                                # Dedicated storage account for function app
    ├── app-insights.bicep                           # Application Insights workspace
    └── key-vault.bicep                              # Secrets + Managed Identity access policy

.github/
└── workflows/
    └── ci-cd.yml                                    # Build → Unit → Integration → What-If → Deploy → Swap
```

**Structure Decision**: 4-project clean architecture (Host / Application / Domain / Infrastructure)
with a shared solution file. Domain has zero infrastructure dependencies and is the primary
coverage target (≥ 80%). Application contains all CQRS handlers and depends only on Domain
interfaces. Infrastructure implements those interfaces against real Azure SDKs.

---

## Complexity Tracking

*No constitution violations — table empty.*

---

## Key Design Decisions

### Parquet Write Strategy

`Parquet.Net` writes all `CanonicalRecord` instances collected in memory after successful
validation. For the specified 10,000-row ceiling this is safe (~50 MB peak at ~5 KB/record).
The write sequence:

1. Build `ParquetSchema` from `VendorSchemaMapping` canonical field definitions.
2. Stage all records in a `List<CanonicalRecord>` after transformation.
3. `DataLakeFileClient.DeleteIfExistsAsync()` on the partition path (idempotent overwrite).
4. `DataLakeFileClient.UploadAsync(parquetStream)` with the full partition.

If step 4 fails, the partition directory has already been deleted — the function retries on
the next Service Bus delivery. DLQ is reached after `maxDeliveryCount`.

### MediatR Notification for Downstream Event

`DatasetBronzeAvailableNotification` is published via `IMediator.Publish()` inside the
command handler only after a confirmed Bronze write. The `DatasetBronzeAvailablePublisher`
notification handler sends the Service Bus message. This pattern:

- Keeps command handler focused on the write operation.
- Makes the Service Bus send independently unit-testable by mocking `IEventPublisher`.
- Preserves the all-or-nothing guarantee: if publish fails, the function throws, the Service
  Bus message is nacked, and retried — Bronze write is idempotent on retry.

### Schema Registry

`VendorSchemaMapping` configurations are stored as JSON blobs at
`schema-registry/{vendor_id}-{schema_version}.json` in a dedicated Azure Blob container.
`BlobSchemaRegistry` caches loaded mappings in memory (keyed by `vendor_id:schema_version`)
for the lifetime of the function instance, avoiding a remote call per invocation.

### CI/CD Pipeline Stages

```text
trigger: push to main

stages:
  1. build          — dotnet build --configuration Release
  2. unit-tests     — dotnet test UnitTests; coverage gate ≥ 80%
  3. integration    — docker-compose up azurite + sb-emulator; dotnet test IntegrationTests
  4. bicep-what-if  — az deployment group what-if (infra PRs only)
  5. deploy-staging — AzureFunctionApp@2 → staging slot
  6. health-check   — availability probe on staging slot
  7. slot-swap      — AzureAppServiceManage@0 staging → production
  8. otel-validate  — query App Insights for OTEL_SERVICE_NAME trace (non-blocking warning)
```

Auth: Workload Identity Federation (OIDC) service connection — no long-lived secrets.
