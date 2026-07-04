# Implementation Plan: Dataset Ingestion Pipeline (Raw → Bronze)

**Branch**: `main` | **Date**: 2026-03-24 | **Spec**: [specs/001-dataset-ingestion-pipeline/spec.md](../001-dataset-ingestion-pipeline/spec.md)
**Input**: Feature specification from `/specs/001-dataset-ingestion-pipeline/spec.md`

---

## Summary

Implement a .NET 10 Isolated Worker Azure Function that consumes `dataset.available` events from
Azure Service Bus, retrieves vendor CSV files from ADLS Gen2, validates and transforms them to a
canonical Parquet schema (derived from the `VendorSchemaMapping` configuration), and writes the
result atomically to the Bronze layer — then publishes a `dataset.bronze.available` event for
downstream Silver-layer processing.

---

## Technical Context

**Language/Version**: .NET 10 (LTS) Isolated Worker
**Primary Dependencies**: MediatR 12.x, CsvHelper 33.x, Parquet.Net 4.23.x, Azure.Storage.Files.DataLake 12.18.x, Azure.Storage.Blobs 12.x, Azure.Messaging.ServiceBus 7.18.x, OpenTelemetry 1.10.x, Microsoft.Extensions.Resilience (Polly v8)
**Storage**: ADLS Gen2 (`DataLakeServiceClient`) for input reads + Bronze writes; Azure Blob Storage for schema registry; Azurite + Microsoft Service Bus Emulator (Docker) for local dev
**Testing**: xUnit + Moq + FluentAssertions; Azurite + Microsoft Service Bus Emulator in CI
**Target Platform**: Azure Functions v4, Premium Plan (EP1), Windows
**Project Type**: Event-driven background processing function
**Performance Goals**: ≤ 60 s end-to-end for ≤ 10,000-row dataset; 50 concurrent events without loss
**Constraints**: Managed Identity only (no shared-key connection strings in production); `telemetryMode: OpenTelemetry`; `TreatWarningsAsErrors` enabled
**Scale/Scope**: Single function app; up to 50 concurrent Service Bus messages
**OpenTelemetry Exporter**: `Azure.Monitor.OpenTelemetry.Exporter` + `Microsoft.Azure.Functions.Worker.OpenTelemetry`
**CI/CD Platform**: GitHub Actions — `AzureFunctionApp@2`; Workload Identity Federation (OIDC)

---

## Constitution Check

*GATE: Must pass before Phase 0 research.*

### OTel Gates (Principles V & IX)

- [x] `host.json` includes `"telemetryMode": "OpenTelemetry"` ✅
- [x] `Microsoft.Azure.Functions.Worker.OpenTelemetry` package present ✅
- [x] `OTEL_SERVICE_NAME` defined in `local.settings.json` ✅ (must also appear in Bicep for all environments)
- [x] `.UseFunctionsWorkerDefaults()` in `Program.cs` ✅
- [ ] **VIOLATION**: `Azure.Monitor.OpenTelemetry.AspNetCore` (Azure Monitor Distro) is referenced in the host `.csproj`. Principle IX explicitly forbids this in isolated worker functions — it adds `AspNetCoreInstrumentation` that duplicates request spans from the Functions host. Must be replaced by `Azure.Monitor.OpenTelemetry.Exporter` only (already present). See Complexity Tracking row 1.
- [ ] **VIOLATION**: `Microsoft.ApplicationInsights.WorkerService` and `Microsoft.Azure.Functions.Worker.ApplicationInsights` packages are present alongside the OTel path. Principle VIII forbids co-existence of the App Insights worker service with the OTel exporter pattern. Both packages must be removed. See Complexity Tracking row 2.
- [ ] **VIOLATION**: `Program.cs` is missing the `LoggerFilterOptions` rule removal that strips the App Insights `Warning+` filter, required by Principle VIII. All log levels must flow to Application Insights. See Complexity Tracking row 3.
- [ ] `ConsoleExporter` absent ✅

### CI/CD Gates (Constitution Workflow §8)

- [ ] Workload Identity Federation (OIDC) service connection — **NOT YET IMPLEMENTED** (no pipeline YAML exists)
- [ ] Unit + Integration test stage with ≥ 80% coverage gate — **NOT YET IMPLEMENTED**
- [ ] Bicep what-if stage — **NOT YET IMPLEMENTED**
- [ ] Staging slot deploy + health check — **NOT YET IMPLEMENTED**
- [ ] `AzureFunctionApp@2` task — **NOT YET IMPLEMENTED**

### Additional Code Violations (not constitution, but spec-mandated)

- [ ] **FR-001a VIOLATION**: Service Bus topic `"dataset-events"` and subscription `"dataset-ingestion"` are hardcoded in the `[ServiceBusTrigger]` attribute. Must switch to a queue-based trigger using `%SERVICEBUS_QUEUE_NAME%` (Basic tier — no topics/subscriptions); `ServiceBusEventPublisher` must send to `%SERVICEBUS_BRONZE_QUEUE_NAME%` queue.
- [ ] **FR-010 VIOLATION**: `ColumnMapping` model has no `MinValue`/`MaxValue` fields. `DataQualityValidator` checks numeric parseability but not configured range boundaries. Both must be added.
- [ ] **FR-017b VIOLATION**: `OneLakeBronzeWriter` writes only 5 fixed columns. Must derive schema from `VendorSchemaMapping.ColumnMappings` and accept mapping as parameter on `WriteAsync`.
- [ ] **FR-021 VIOLATION**: `ProcessingMetricsEmitter` is registered in DI but never injected into `ProcessDatasetCommandHandler`. Metrics are not emitted.
- [ ] **FR-023 VIOLATION**: Duplicate `DatasetActivitySource` in both `Domain.Telemetry` and `Infrastructure.Telemetry`. Handler uses Domain version; `Program.cs` registers Infrastructure version — spans from the handler are not exported.
- [ ] **FR-006c NOT IMPLEMENTED**: Zero-row CSV is not detected or rejected.

---

## Project Structure

### Documentation (this feature)

```text
specs/main/
├── plan.md              ← this file
├── research.md          ← Phase 0 output
├── data-model.md        ← Phase 1 output
├── quickstart.md        ← Phase 1 output
└── contracts/
    ├── dataset-available-event.json
    ├── dataset-bronze-available-event.json
    ├── vendor-schema-mapping.json
    └── dead-letter-reason.json
```

### Source Code

```text
src/
├── DatasetProcessingFunction/                     ← Function host (entry point, DI, triggers)
│   ├── Functions/
│   │   └── ProcessDatasetFunction.cs              ← ServiceBusTrigger (config-driven names)
│   ├── Program.cs                                 ← OTel wiring (Exporter only, no AspNetCore distro)
│   ├── host.json                                  ← telemetryMode: OpenTelemetry
│   └── local.settings.json
│
├── DatasetProcessingFunction.Domain/              ← Business rules, no Azure SDK deps
│   ├── Models/
│   │   └── VendorSchemaMapping.cs                 ← + MinValue/MaxValue on ColumnMapping
│   ├── Services/
│   │   ├── CsvParserService.cs                    ← + empty-dataset rejection (FR-006c)
│   │   ├── DataQualityValidator.cs                ← + range check against min/max (FR-010)
│   │   ├── SchemaTransformer.cs
│   │   └── RecordEnricher.cs
│   ├── Telemetry/
│   │   └── DatasetActivitySource.cs               ← SINGLE source of truth (remove Infrastructure copy)
│   └── ValueObjects/
│       ├── CanonicalRecord.cs
│       ├── RawRecord.cs
│       ├── ValidationResult.cs
│       └── ...
│
├── DatasetProcessingFunction.Application/         ← Use cases, interfaces, MediatR handlers
│   ├── Commands/
│   │   ├── ProcessDatasetCommand.cs
│   │   ├── ProcessDatasetCommandHandler.cs        ← inject ProcessingMetricsEmitter; pass mapping to WriteAsync
│   │   └── ProcessDatasetResult.cs
│   ├── Interfaces/
│   │   ├── IBronzeWriter.cs                       ← WriteAsync signature + VendorSchemaMapping param
│   │   ├── IDatasetReader.cs
│   │   ├── IEventPublisher.cs
│   │   └── ISchemaRegistry.cs
│   └── Notifications/
│       ├── DatasetBronzeAvailableNotification.cs
│       └── DatasetBronzeAvailablePublisher.cs
│
└── DatasetProcessingFunction.Infrastructure/      ← Azure SDK adapters
    ├── Storage/
    │   ├── AdlsDatasetReader.cs                   ← + Polly retry (3 × 2s base, 30s max)
    │   └── OneLakeBronzeWriter.cs                 ← dynamic Parquet schema from VendorSchemaMapping
    ├── Messaging/
    │   └── ServiceBusEventPublisher.cs
    ├── SchemaRegistry/
    │   └── BlobSchemaRegistry.cs
    └── Telemetry/
        └── ProcessingMetricsEmitter.cs            ← remove DatasetActivitySource from here

tests/
├── DatasetProcessingFunction.UnitTests/           ← xUnit, Moq — no Azure deps
│   ├── Domain/
│   │   ├── CsvParserServiceTests.cs               ← + empty CSV, UTF-16, range boundary tests
│   │   ├── DataQualityValidatorTests.cs           ← + min/max range tests
│   │   ├── SchemaTransformerTests.cs
│   │   ├── DatasetProcessingJobTests.cs
│   │   └── DatasetIdTests.cs
│   └── Application/
│       ├── ProcessDatasetCommandHandlerTests.cs   ← + metrics emission, empty CSV path
│       └── DatasetBronzeAvailablePublisherTests.cs
│
└── DatasetProcessingFunction.IntegrationTests/    ← Azurite + Service Bus Emulator
    ├── ProcessDatasetEndToEndTests.cs
    └── fixtures/
        ├── vendor-abc-v2-valid.csv
        ├── vendor-abc-v2-missing-timestamp.csv
        ├── vendor-abc-v2-invalid-range.csv
        └── vendor-abc-v2-empty.csv                ← NEW: header-only fixture

docker-compose.yml                                 ← Azurite + Service Bus Emulator + SQL Server 2022
.env                                               ← CONFIG_PATH, ACCEPT_EULA, MSSQL_SA_PASSWORD (git-ignored)
emulator/
└── Config.json                                    ← Service Bus Emulator topology (namespace: sbemulatorns)
```

**Structure Decision**: Four-project clean-architecture layout already established. No structural changes required — only code fixes within existing boundaries.

---

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
| --- | --- | --- |
| Remove `Azure.Monitor.OpenTelemetry.AspNetCore` | Principle IX — AspNetCore Distro duplicates request spans in isolated worker. Replace with `Azure.Monitor.OpenTelemetry.Exporter` (already referenced). | Cannot keep both — duplicated spans corrupt the Application Map and inflate telemetry costs |
| Remove `Microsoft.ApplicationInsights.WorkerService` + `Microsoft.Azure.Functions.Worker.ApplicationInsights` | Principle VIII/IX — these co-exist with the OTel exporter path, causing duplicate request telemetry | Cannot keep alongside OTel path per constitution; no feature is lost since `Azure.Monitor.OpenTelemetry.Exporter` covers the App Insights sink |
| Add `LoggerFilterOptions` filter removal in `Program.cs` | Principle VIII — without it, ILogger entries below Warning are silently dropped by the App Insights logger provider | No simpler workaround; this is a required boilerplate for isolated worker + OTel |

---

## Implementation Phases

### Phase 1 — Fix Constitution & Spec Violations (pre-condition for everything else)

These are bugs in the existing skeleton, not new features. Must be fixed before any new work.

| # | Change | File(s) | Spec Ref |
| --- | --- | --- | --- |
| 1 | Remove `Azure.Monitor.OpenTelemetry.AspNetCore`, keep `Azure.Monitor.OpenTelemetry.Exporter`; remove `Microsoft.ApplicationInsights.WorkerService` + `Microsoft.Azure.Functions.Worker.ApplicationInsights`; update `Program.cs` OTel wiring | `DatasetProcessingFunction.csproj`, `Program.cs` | Constitution VIII/IX |
| 2 | Add `LoggerFilterOptions` filter removal to `Program.cs` | `Program.cs` | Constitution VIII |
| 3 | Remove duplicate `DatasetActivitySource` from `Infrastructure.Telemetry`; update `Program.cs` to use Domain source | `Infrastructure/Telemetry/DatasetActivitySource.cs`, `Program.cs` | FR-023 |
| 4 | Replace topic+subscription `[ServiceBusTrigger]` with queue-based trigger using `%SERVICEBUS_QUEUE_NAME%`; remove `subscriptionName` argument; update `ServiceBusEventPublisher` to send to `%SERVICEBUS_BRONZE_QUEUE_NAME%` queue | `ProcessDatasetFunction.cs`, `ServiceBusEventPublisher.cs` | FR-001a, FR-025 |

### Phase 2 — Domain Model Fixes

| # | Change | File(s) | Spec Ref |
| --- | --- | --- | --- |
| 5 | Add `MinValue`/`MaxValue` (`decimal?`) to `ColumnMapping` record | `VendorSchemaMapping.cs` | FR-010 |
| 6 | Add range-boundary check to `DataQualityValidator` using `MinValue`/`MaxValue` | `DataQualityValidator.cs` | FR-010 |
| 6b | Create `UnknownSchemaException.cs` in `Domain/Exceptions/` carrying `VendorId` and `SchemaVersion` properties; throw from `BlobSchemaRegistry` when mapping lookup returns null; catch in `ProcessDatasetFunction.cs` dead-letter handler with `error_type: "UnknownSchema"` | `UnknownSchemaException.cs`, `BlobSchemaRegistry.cs`, `ProcessDatasetFunction.cs` | FR-011 |
| 7 | Add empty-dataset detection to `CsvParserService`; throw `EmptyDatasetException`; handle in function trigger (dead-letter as `EmptyDataset`) | `CsvParserService.cs`, `ProcessDatasetFunction.cs`, new `EmptyDatasetException.cs` | FR-006c |

### Phase 3 — Bronze Writer Overhaul (FR-017b)

| # | Change | File(s) | Spec Ref |
| --- | --- | --- | --- |
| 8 | Update `IBronzeWriter.WriteAsync` signature to accept `VendorSchemaMapping` | `IBronzeWriter.cs` | FR-017b |
| 8b | Implement unit conversion in `SchemaTransformer.cs` for all `UnitConversion` keys (`kw_to_w` × 1000, `mw_to_w` × 1,000,000, `kwh_to_wh` × 1000, `mwh_to_wh` × 1,000,000); pass-through when key is absent | `SchemaTransformer.cs` | FR-015 |
| 9 | Rewrite `OneLakeBronzeWriter.WriteParquetAsync` to build `ParquetSchema` dynamically from `VendorSchemaMapping.ColumnMappings` + 5 fixed enrichment columns; use `UploadAsync(overwrite: true)` for atomic partition replace | `OneLakeBronzeWriter.cs` | FR-017b, FR-018 |
| 10 | Update `ProcessDatasetCommandHandler` to pass `mapping` to `WriteAsync` | `ProcessDatasetCommandHandler.cs` | FR-017b |

### Phase 4 — ADLS Retry + Metrics Wiring

| # | Change | File(s) | Spec Ref |
| --- | --- | --- | --- |
| 11 | Add `Microsoft.Extensions.Resilience` package; wrap `AdlsDatasetReader.ReadAsync` with Polly retry (3 × 2s base, 30s max, transient errors only) | `DatasetProcessingFunction.Infrastructure.csproj`, `AdlsDatasetReader.cs` | FR-005 |
| 12 | Inject `ProcessingMetricsEmitter` into `ProcessDatasetCommandHandler`; emit metrics after handler completes | `ProcessDatasetCommandHandler.cs`, `Program.cs` | FR-021 |
| 13 | Register `RecordEnricher` in DI instead of `new RecordEnricher()` in handler | `Program.cs`, `ProcessDatasetCommandHandler.cs` | Constitution II (DI) |

### Phase 5 — Local Dev Infrastructure

| # | Change | File(s) | Spec Ref |
| --- | --- | --- | --- |
| 14 | Add `docker-compose.yml` + `.env` + `emulator/Config.json` (Azurite + Service Bus Emulator + SQL Server 2022) | `docker-compose.yml`, `.env`, `emulator/Config.json` | FR-001 / Assumption |
| 15 | Replace `ServiceBusConnection__fullyQualifiedNamespace` with `ServiceBusConnection` SAS string in `local.settings.json`; add dual-mode `ServiceBusClient` construction in `Program.cs` | `local.settings.json`, `Program.cs` | FR-001, research §2 |

### Phase 6 — Tests

| # | Change | File(s) | Spec Ref |
| --- | --- | --- | --- |
| 16 | Add unit tests for range validation, empty CSV, dynamic Parquet schema, metrics emission | `UnitTests/Domain/`, `UnitTests/Application/` | SC-007 |
| 17 | Add integration test fixture `vendor-abc-v2-empty.csv`; add empty-dataset test | `IntegrationTests/fixtures/`, `ProcessDatasetEndToEndTests.cs` | FR-006c |
| 18 | Add integration test for Parquet output column completeness | `ProcessDatasetEndToEndTests.cs` | SC-006 |

### Phase 7 — CI/CD Pipeline

| # | Change | File(s) | Spec Ref |
| --- | --- | --- | --- |
| 19 | Create GitHub Actions workflow with build → unit test (≥80% gate) → integration test → Bicep what-if → staging deploy → health check → slot swap stages | `.github/workflows/deploy.yml` | FR-024, Constitution §8 |
