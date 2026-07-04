# Implementation Plan: Dataset Ingestion Pipeline (Raw → Bronze)

**Branch**: `002-dataset-ingestion-pipeline` | **Date**: 2026-04-16 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/002-dataset-ingestion-pipeline/spec.md`

---

## Summary

Implement a .NET 10 Isolated Worker Azure Function that consumes `dataset.available` events
from an Azure Service Bus **queue** (`raw-energy-events`), retrieves vendor CSV files from
ADLS Gen2, validates and transforms them to a canonical Parquet schema (derived from
`VendorSchemaMapping` configuration), applies unit conversions, and writes the result
atomically to the Bronze layer — then publishes a `dataset.bronze.available` event to the
`dataset-bronze-available` queue for downstream Silver-layer processing.

---

## Technical Context

**Language/Version**: .NET 10 (LTS) Isolated Worker
**Primary Dependencies**: MediatR 12.x, CsvHelper 33.x, Parquet.Net 4.23.x, Azure.Storage.Files.DataLake 12.18.x, Azure.Storage.Blobs 12.x, Azure.Messaging.ServiceBus 7.18.x, OpenTelemetry 1.10.x, Microsoft.Extensions.Resilience (Polly v8)
**Storage**: ADLS Gen2 (`DataLakeServiceClient`) for input reads + Bronze writes; Azure Blob Storage for schema registry; Azurite + Microsoft Service Bus Emulator (Docker) for local dev
**Testing**: xUnit + Moq + FluentAssertions; Azurite + Microsoft Service Bus Emulator in CI
**Target Platform**: Azure Functions v4, Premium Plan (EP1), Windows
**Project Type**: Event-driven background processing function
**Performance Goals**: ≤ 60 s end-to-end for ≤ 10,000-row dataset; 50 concurrent events without loss
**Constraints**: Managed Identity only (no shared-key connection strings in production); `telemetryMode: OpenTelemetry`; `TreatWarningsAsErrors` enabled; Service Bus Basic tier (queues only — no topics or subscriptions)
**Scale/Scope**: Single function app; up to 50 concurrent Service Bus messages
**OpenTelemetry Exporter**: `Azure.Monitor.OpenTelemetry.Exporter` + `Microsoft.Azure.Functions.Worker.OpenTelemetry`
**CI/CD Platform**: GitHub Actions — `AzureFunctionApp@2`; Workload Identity Federation (OIDC) via `azure/login@v2`

---

## Constitution Check

*GATE: Must pass before Phase 0 research.*

### OTel Gates (Principles V & IX)

- [x] `host.json` includes `"telemetryMode": "OpenTelemetry"` ✅
- [x] `Microsoft.Azure.Functions.Worker.OpenTelemetry` package present in `.csproj` ✅
- [x] `OTEL_SERVICE_NAME` defined in `local.settings.json` ✅ (must also appear in Bicep app settings)
- [x] `.UseFunctionsWorkerDefaults().UseAzureMonitorExporter()` planned in `Program.cs` ✅
- [x] `Azure.Monitor.OpenTelemetry.AspNetCore` (AspNetCore Distro) absent from `.csproj` ✅
- [x] `Microsoft.ApplicationInsights.WorkerService` absent from `.csproj` ✅
- [x] `ConsoleExporter` absent ✅
- [x] `LoggerFilterOptions` filter removal for App Insights `Warning+` filter planned in `Program.cs` ✅

### CI/CD Gates (Constitution Workflow §8)

- [x] Workload Identity Federation (OIDC) — `permissions: id-token: write` present in `ci-cd.yml` ✅
- [ ] Unit + Integration test stage with ≥ 80% coverage gate — **pipeline exists but coverage gate threshold not yet enforced**
- [ ] Bicep what-if stage — **NOT YET IMPLEMENTED** (no Bicep files exist)
- [ ] Staging slot deploy + health check — **NOT YET IMPLEMENTED**
- [ ] `AzureFunctionApp@2` task — **NOT YET IMPLEMENTED** (deploy stage not yet in pipeline)

### Remaining Spec Violations (not constitution, but spec-mandated)

- [ ] **FR-013a**: `BlobSchemaRegistry` has no Polly retry; schema registry unavailability is not handled. Must apply same 3-attempt exponential retry as ADLS reads.
- [ ] **FR-015**: Unit conversion (`kw_to_w`, `mw_to_w`, `kwh_to_wh`, `mwh_to_wh`) not implemented in `SchemaTransformer.cs`.
- [ ] **FR-011 / US2-S3**: `UnknownSchemaException` not yet created; `BlobSchemaRegistry` does not throw on null lookup; dead-letter catch block in `ProcessDatasetFunction.cs` does not handle it.
- [ ] **FR-016 / FR-011**: `site_id` derivation rule — must come from CSV column with `canonical_field: "site_id"`; vendor schema missing this mapping triggers UnknownSchema.
- [ ] **FR-016a**: `timestamp` canonical field mapping is a schema-level requirement; a `VendorSchemaMapping` with no `canonical_field: "timestamp"` triggers UnknownSchema (same treatment as `site_id`).
- [ ] **FR-003**: `correlation_id` is optional in `DatasetAvailableEvent`; if absent the function MUST generate a new `Guid`-based `correlation_id` and log a warning — not dead-letter.
- [ ] **Constitution IV / C4**: No Bicep infrastructure files exist for any Azure resource.
- [ ] **Constitution VIII / C5**: No Warmup trigger implemented.

---

## Project Structure

### Documentation (this feature)

```text
specs/002-dataset-ingestion-pipeline/
├── plan.md              ← this file
├── research.md          ← Phase 0 output
├── data-model.md        ← Phase 1 output
├── quickstart.md        ← Phase 1 output
├── checklists/
│   └── requirements.md
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
│   │   ├── ProcessDatasetFunction.cs              ← ServiceBusTrigger (queue: %SERVICEBUS_QUEUE_NAME%)
│   │   └── WarmupFunction.cs                      ← WarmupTrigger (Premium plan — Constitution VIII)
│   ├── Program.cs                                 ← OTel wiring; dual-mode SB client; DI registrations
│   ├── host.json                                  ← telemetryMode: OpenTelemetry; SB extension settings
│   └── local.settings.json
│
├── DatasetProcessingFunction.Domain/              ← Business rules, no Azure SDK deps
│   ├── Models/
│   │   └── VendorSchemaMapping.cs                 ← ColumnMapping with MinValue/MaxValue
│   ├── Services/
│   │   ├── CsvParserService.cs                    ← UTF-8 check; empty-dataset detection
│   │   ├── DataQualityValidator.cs                ← timestamp + range validation
│   │   ├── SchemaTransformer.cs                   ← canonical mapping + unit conversion (FR-015)
│   │   └── RecordEnricher.cs                      ← site_id from Fields["site_id"]; ingestion_time; source_dataset_id; schema_version
│   ├── Telemetry/
│   │   └── DatasetActivitySource.cs               ← SINGLE activity source (Domain layer only)
│   ├── Exceptions/
│   │   ├── EmptyDatasetException.cs
│   │   └── UnknownSchemaException.cs              ← VendorId + SchemaVersion properties (FR-011)
│   └── ValueObjects/
│       ├── CanonicalRecord.cs
│       ├── RawRecord.cs
│       └── ValidationResult.cs
│
├── DatasetProcessingFunction.Application/         ← Use cases, interfaces, MediatR handlers
│   ├── Commands/
│   │   ├── ProcessDatasetCommand.cs
│   │   ├── ProcessDatasetCommandHandler.cs        ← injects ProcessingMetricsEmitter; passes mapping to WriteAsync
│   │   └── ProcessDatasetResult.cs
│   └── Interfaces/
│       ├── IBronzeWriter.cs                       ← WriteAsync(DatasetId, DateOnly, records, VendorSchemaMapping, ct)
│       ├── IDatasetReader.cs
│       ├── IEventPublisher.cs
│       ├── ISchemaRegistry.cs
│       └── IProcessingMetricsEmitter.cs
│
└── DatasetProcessingFunction.Infrastructure/      ← Azure SDK adapters
    ├── Storage/
    │   ├── AdlsDatasetReader.cs                   ← Polly "adls-read" pipeline (FR-005)
    │   └── OneLakeBronzeWriter.cs                 ← dynamic Parquet schema; UploadAsync(overwrite:true)
    ├── Messaging/
    │   └── ServiceBusEventPublisher.cs            ← sends to SERVICEBUS_BRONZE_QUEUE_NAME
    ├── SchemaRegistry/
    │   └── BlobSchemaRegistry.cs                  ← Polly "schema-registry-read" pipeline (FR-013a); validates site_id + timestamp mappings
    └── Telemetry/
        └── ProcessingMetricsEmitter.cs

infrastructure/
├── main.bicep
├── main.parameters.json
└── modules/
    ├── functionApp.bicep
    ├── serviceBus.bicep
    ├── storage.bicep
    ├── appInsights.bicep
    └── keyVault.bicep

tests/
├── DatasetProcessingFunction.UnitTests/
│   ├── Domain/
│   │   ├── CsvParserServiceTests.cs
│   │   ├── DataQualityValidatorTests.cs
│   │   ├── SchemaTransformerTests.cs              ← unit conversion tests (FR-015)
│   │   └── RecordEnricherTests.cs
│   ├── Application/
│   │   └── ProcessDatasetCommandHandlerTests.cs
│   └── Infrastructure/
│       ├── OneLakeBronzeWriterTests.cs
│       └── ProcessingMetricsEmitterTests.cs
│
└── DatasetProcessingFunction.IntegrationTests/
    ├── ProcessDatasetEndToEndTests.cs
    └── fixtures/
        ├── vendor-abc-v2-valid.csv
        ├── vendor-abc-v2-schema.json
        ├── vendor-abc-v2-missing-timestamp.csv
        ├── vendor-abc-v2-invalid-range.csv
        ├── vendor-abc-v2-empty.csv
        └── vendor-unknown-schema.csv              ← unrecognised header (US2 Scenario 3)

docker-compose.yml                                 ← Azurite + Service Bus Emulator + SQL Server 2022
.env                                               ← CONFIG_PATH, ACCEPT_EULA, MSSQL_SA_PASSWORD (git-ignored)
emulator/
└── Config.json                                    ← queues: raw-energy-events, dataset-bronze-available
```

**Structure Decision**: Four-project clean-architecture layout already established. Bicep
infrastructure modules added under `infrastructure/`. No other structural changes required.

---

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
| --- | --- | --- |
| Dual-mode `ServiceBusClient` construction in `Program.cs` | Service Bus emulator does not support Managed Identity; production MUST use `DefaultAzureCredential` — cannot use a single construction path | A single SAS connection string in production violates Constitution VI; a single Managed Identity path breaks local dev |
| Polly retry on both ADLS reads and schema registry reads | FR-005 and FR-013a mandate retry for both critical external reads; Azure SDK built-in retry must be disabled to prevent multiplicative attempts | Cannot rely on Azure SDK retry alone (no domain-level permanent/transient classification); cannot skip retry on schema registry (transient outage would prematurely DLQ events) |
| `UploadAsync(overwrite: true)` rather than append | FR-018 mandates idempotent atomic replace; append would duplicate records on re-delivery | No merge/upsert API exists for Parquet at the ADLS file level; atomic overwrite is the only correct idempotency mechanism |

---

## Implementation Phases

### Phase 1 — Setup (Local Dev Infrastructure)

New files only — no existing code touched.

| # | Change | File(s) | Spec Ref |
| --- | --- | --- | --- |
| 1 | Create `docker-compose.yml` with Azurite (10000/10001/10002), Service Bus Emulator (5672/5300), and SQL Server 2022; `sb-emulator` network; `depends_on` wiring | `docker-compose.yml` | FR-001, research §2 |
| 2 | Create `.env` with `CONFIG_PATH`, `ACCEPT_EULA=Y`, `MSSQL_SA_PASSWORD`; add to `.gitignore` | `.env` | research §2 |
| 3 | Create `emulator/Config.json` with `sbemulatorns` namespace and two **queues**: `raw-energy-events`, `dataset-bronze-available`; `MaxDeliveryCount: 3`, `LockDuration: PT1M` | `emulator/Config.json` | FR-001, FR-025 |

**Checkpoint**: `docker compose up -d` starts all three containers cleanly

---

### Phase 2 — Foundational Fixes (Constitution & Spec Violations)

Blocking prerequisites — no user story work begins until complete.

| # | Change | File(s) | Spec Ref |
| --- | --- | --- | --- |
| 4 | Remove `Azure.Monitor.OpenTelemetry.AspNetCore`, `Microsoft.ApplicationInsights.WorkerService`, `Microsoft.Azure.Functions.Worker.ApplicationInsights` from `.csproj` if present | `DatasetProcessingFunction.csproj` | Constitution VIII/IX |
| 5 | Fix `Program.cs`: (a) `UseAzureMonitorExporter()` only; (b) `LoggerFilterOptions` filter removal; (c) dual-mode `ServiceBusClient` from research §2 | `Program.cs` | Constitution VIII, FR-001 |
| 6 | Delete `Infrastructure/Telemetry/DatasetActivitySource.cs` (duplicate); update `Program.cs` `.AddSource(...)` to reference Domain source | `Program.cs`, `Infrastructure/Telemetry/DatasetActivitySource.cs` | FR-023 |
| 7 | Replace `[ServiceBusTrigger]` with queue-based trigger `%SERVICEBUS_QUEUE_NAME%`; remove `subscriptionName` argument | `ProcessDatasetFunction.cs` | FR-001a |
| 8 | Add `MinValue`/`MaxValue` (`decimal?`) to `ColumnMapping` record; guard: `MinValue ≤ MaxValue` if both non-null | `VendorSchemaMapping.cs` | FR-010 |
| 9 | Update `local.settings.json`: `SERVICEBUS_QUEUE_NAME=raw-energy-events`, `SERVICEBUS_BRONZE_QUEUE_NAME=dataset-bronze-available`; remove topic/subscription settings | `local.settings.json` | FR-001a, FR-025 |
| 10 | Configure `host.json` Service Bus extension: `autoComplete: false`, `prefetchCount: 16`, `maxConcurrentCalls: 16`, `maxAutoLockRenewalDuration: "00:05:00"` | `host.json` | Constitution VIII |
| 10a | Add `correlation_id` fallback to `ProcessDatasetFunction.cs`: if `DatasetAvailableEvent.CorrelationId` is null/empty, generate `Guid.NewGuid().ToString()` and log a warning before dispatching command | `ProcessDatasetFunction.cs` | FR-003 |

**Checkpoint**: `dotnet build` passes with zero warnings; `func start` binds to emulator queue

---

### Phase 3 — User Story 1: Successful End-to-End Processing (MVP)

| # | Change | File(s) | Spec Ref |
| --- | --- | --- | --- |
| 11 | Update `IBronzeWriter.WriteAsync` signature: `WriteAsync(DatasetId, DateOnly, IReadOnlyList<CanonicalRecord>, VendorSchemaMapping, CancellationToken)` | `IBronzeWriter.cs` | FR-017b |
| 12 | Rewrite `OneLakeBronzeWriter`: dynamic Parquet schema from `VendorSchemaMapping.ColumnMappings`; enrichment columns first; `UploadAsync(overwrite: true)` | `OneLakeBronzeWriter.cs` | FR-017b, FR-018 |
| 13 | Implement unit conversion in `SchemaTransformer.cs` — `kw_to_w` (×1000), `mw_to_w` (×1 000 000), `kwh_to_wh` (×1000), `mwh_to_wh` (×1 000 000); pass-through if key absent | `SchemaTransformer.cs` | FR-015 |
| 14 | Update `RecordEnricher.cs`: read `site_id` value from `CanonicalRecord.Fields["site_id"]` (set by `SchemaTransformer` via the `site_id` canonical field mapping) | `RecordEnricher.cs` | FR-016 |
| 15 | Add schema-level validation to `BlobSchemaRegistry`: if no `ColumnMapping` has `canonical_field: "site_id"` **or** `canonical_field: "timestamp"`, throw `UnknownSchemaException` | `BlobSchemaRegistry.cs` | FR-016, FR-016a, FR-011 |
| 16 | Register Polly `"adls-read"` pipeline in `Program.cs`; set `DataLakeClientOptions.Retry.MaxRetries = 0`; wrap `AdlsDatasetReader.ReadAsync` in pipeline | `Program.cs`, `AdlsDatasetReader.cs` | FR-005 |
| 17 | Add `"schema-registry-read"` Polly pipeline in `Program.cs`; wrap `BlobSchemaRegistry.GetMappingAsync` call; 404 is non-retriable (throw `UnknownSchemaException`) | `Program.cs`, `BlobSchemaRegistry.cs` | FR-013a |
| 18 | Register `RecordEnricher` in DI (Singleton/Transient); remove `new RecordEnricher()` instantiation from handler | `Program.cs`, `ProcessDatasetCommandHandler.cs` | Constitution II |
| 19 | Update `ProcessDatasetCommandHandler`: inject `ProcessingMetricsEmitter`; pass `mapping` to `WriteAsync`; emit metrics after write | `ProcessDatasetCommandHandler.cs` | FR-017b, FR-021 |
| 20 | Verify `ServiceBusEventPublisher` sends to `SERVICEBUS_BRONZE_QUEUE_NAME` queue with required snake_case fields | `ServiceBusEventPublisher.cs` | FR-025, FR-026 |
| 21 | Implement `WarmupFunction.cs` with `[WarmupTrigger]` to pre-load DI on scale-out | `WarmupFunction.cs` | Constitution VIII |

**Tests for US1:**

| # | Test | File(s) | Spec Ref |
| --- | --- | --- | --- |
| 22 | Unit tests: dynamic Parquet schema (enrichment + vendor columns); `datetime` = `DateTimeDataField`; `decimal` = `DecimalDataField(18,6)`; duplicates skipped | `OneLakeBronzeWriterTests.cs` | FR-017b |
| 23 | Unit tests: unit conversions (`kw_to_w`, `mw_to_w`, `kwh_to_wh`, `mwh_to_wh`); null conversion = pass-through | `SchemaTransformerTests.cs` | FR-015 |
| 24 | Unit tests: `ProcessDatasetCommandHandler` calls `WriteAsync` with mapping; `Emit` called with correct counts; event published after write; event NOT published when write throws | `ProcessDatasetCommandHandlerTests.cs` | FR-017b, FR-021, FR-026 |
| 25 | Integration test: seed schema + valid CSV in Azurite; send event to emulator queue; assert Bronze Parquet exists with all vendor columns + enrichment; re-send and assert single partition (idempotency) | `ProcessDatasetEndToEndTests.cs` | SC-001, SC-002, SC-006 |
| 25a | Unit test: absent `correlation_id` on inbound event → new Guid generated; warning logged; processing completes normally | `ProcessDatasetFunctionTests.cs` | FR-003 |

**Checkpoint**: End-to-end test verifies Bronze Parquet with all canonical columns and atomic overwrite

---

### Phase 4 — User Story 2: Data Quality Failure Handling

| # | Change | File(s) | Spec Ref |
| --- | --- | --- | --- |
| 26 | Create `UnknownSchemaException.cs` with `VendorId` and `SchemaVersion` properties | `Domain/Exceptions/UnknownSchemaException.cs` | FR-011 |
| 27 | Add empty-dataset detection to `CsvParserService`: after reading header, if no data rows throw `EmptyDatasetException` | `CsvParserService.cs` | FR-006c |
| 28 | Add UTF-8 encoding check to `CsvParserService`; throw `UnsupportedEncodingException` with detected encoding | `CsvParserService.cs` | FR-006b |
| 29 | Add range-boundary check to `DataQualityValidator`: for `decimal`/`double`/`float` columns with `MinValue`/`MaxValue` set, validate and add `NumericRange` failure | `DataQualityValidator.cs` | FR-010 |
| 30 | Update `ProcessDatasetFunction.cs` dead-letter catch blocks: `EmptyDatasetException` → `EmptyDataset`; `UnsupportedEncodingException` → `UnsupportedEncoding`; `DatasetValidationException` → `ValidationFailed` (first 10 failures); `UnknownSchemaException` → `UnknownSchema` with `vendor_id` + `schema_version`; all match `contracts/dead-letter-reason.json` | `ProcessDatasetFunction.cs` | FR-003, FR-006b, FR-006c, FR-011, FR-019 |

**Tests for US2:**

| # | Test | File(s) | Spec Ref |
| --- | --- | --- | --- |
| 31 | Unit tests: value below `MinValue` → `NumericRange` failure; above `MaxValue` → `NumericRange` failure; within bounds → pass; null bounds → no check | `DataQualityValidatorTests.cs` | FR-010 |
| 32 | Unit tests: header-only CSV → `EmptyDatasetException`; non-UTF-8 bytes → `UnsupportedEncodingException`; mixed line endings → no exception | `CsvParserServiceTests.cs` | FR-006b, FR-006c |
| 33 | Integration test: empty CSV fixture → dead-letter `error_type: "EmptyDataset"`; no Bronze blob | `ProcessDatasetEndToEndTests.cs` | FR-006c, SC-003 |
| 34 | Integration test: unknown-schema CSV fixture (`vendor-unknown-schema.csv`) → dead-letter `error_type: "UnknownSchema"` with `vendor_id`/`schema_version`; no Bronze blob | `ProcessDatasetEndToEndTests.cs` | FR-011, US2-S3 |
| 34a | Unit test: schema missing `canonical_field: "timestamp"` → `BlobSchemaRegistry` throws `UnknownSchemaException`; dead-letter `error_type: "UnknownSchema"` | `BlobSchemaRegistryTests.cs` | FR-016a |

**Checkpoint**: All dead-letter paths produce correct structured DLQ payloads; Bronze layer empty for all failure scenarios

---

### Phase 5 — User Story 3: Observability

| # | Change | File(s) | Spec Ref |
| --- | --- | --- | --- |
| 35 | Add named Activity spans to `CsvParserService` (`dataset.csv.parse`), `DataQualityValidator` (`dataset.validation.run`), `OneLakeBronzeWriter` (`dataset.bronze.write`); all from Domain `DatasetActivitySource.Source`; `dataset_id` as span attribute | Domain services, `OneLakeBronzeWriter.cs` | FR-023 |
| 36 | Verify `ProcessingMetricsEmitter` defines `Counter<long>` for `records_processed`, `validation_pass_count`, `validation_fail_count`; `Histogram<long>` for `processing_duration_ms`; each instrument tagged with `dataset_id` | `ProcessingMetricsEmitter.cs` | FR-021 |
| 37 | Propagate `CorrelationId` from `DatasetAvailableEvent` (or generated fallback) as structured log scope and as OTel span attribute `correlation_id` in `ProcessDatasetFunction.cs` and `ProcessDatasetCommandHandler.cs` | `ProcessDatasetFunction.cs`, `ProcessDatasetCommandHandler.cs` | FR-020 |

**Tests for US3:**

| # | Test | File(s) | Spec Ref |
| --- | --- | --- | --- |
| 38 | Unit tests: `ProcessingMetricsEmitter.Emit` invokes all 4 instruments with correct values and `dataset_id` tag | `ProcessingMetricsEmitterTests.cs` | FR-021 |
| 39 | Unit test: `CorrelationId` from event appears as attribute on active Activity span in handler | `ProcessDatasetCommandHandlerTests.cs` | FR-020 |

**Checkpoint**: Named child spans visible in local OTel output; `records_processed` counter increments per run

---

### Phase 6 — CI/CD Pipeline

| # | Change | File(s) | Spec Ref |
| --- | --- | --- | --- |
| 40 | Extend `.github/workflows/ci-cd.yml` with sequential stages: **Build** → **Unit Tests** (≥ 80% Domain + Application coverage gate) → **Integration Tests** (docker compose) → **Bicep What-If** → **Deploy to Staging Slot** (`AzureFunctionApp@2`) → **Health Check** → **Slot Swap** (`AzureAppServiceManage@0`) | `.github/workflows/ci-cd.yml` | FR-024, Constitution §8 |
| 41 | Add post-deploy observability validation: query App Insights for trace tagged `OTEL_SERVICE_NAME=dataset-processing-func` within last 30 seconds | `.github/workflows/ci-cd.yml` | SC-004, Constitution §8 |

---

### Phase 7 — Bicep Infrastructure

| # | Change | File(s) | Spec Ref |
| --- | --- | --- | --- |
| 42 | Create `infrastructure/modules/functionApp.bicep`: Function App (Premium EP1), `WEBSITE_RUN_FROM_PACKAGE=1`, all app settings as Key Vault references, `OTEL_SERVICE_NAME` | `infrastructure/` | Constitution IV, VIII |
| 43 | Create `infrastructure/modules/serviceBus.bicep`: Service Bus namespace (Basic tier), `raw-energy-events` queue, `dataset-bronze-available` queue, DLQ alert on depth > 0 | `infrastructure/` | Constitution IV, V, FR-001 |
| 44 | Create `infrastructure/modules/storage.bicep`: ADLS Gen2 account (HNS enabled) for Bronze; Blob account for schema registry; separate from `AzureWebJobsStorage` | `infrastructure/` | Constitution IV, VIII, FR-017a |
| 45 | Create `infrastructure/modules/appInsights.bicep`: Application Insights workspace; error-rate alert; availability test for staging slot health check | `infrastructure/` | Constitution IV, V |
| 46 | Create `infrastructure/modules/keyVault.bicep`: Key Vault; RBAC `Key Vault Secrets User` for Function App managed identity; store all secrets | `infrastructure/` | Constitution IV, VI |
| 47 | Create `infrastructure/main.bicep` + `main.parameters.json`; update `azure.yaml` for AZD | `infrastructure/` | Constitution IV |

---

### Phase 8 — Polish

| # | Change | File(s) | Spec Ref |
| --- | --- | --- | --- |
| 48 | Scan all `src/` files for `Console.WriteLine`, `Debug.WriteLine`; replace with `ILogger` | All production files | FR-022 |
| 49 | End-to-end quickstart validation: `docker compose up -d` → seed schema + CSV → `func start` → publish event → assert Bronze blob + outbound queue message | quickstart.md | SC-001 |
