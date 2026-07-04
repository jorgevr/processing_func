# Tasks: Dataset Ingestion Pipeline (Raw → Bronze)

**Input**: Design documents from `/specs/002-dataset-ingestion-pipeline/`
**Prerequisites**: plan.md ✅, spec.md ✅, research.md ✅, data-model.md ✅, contracts/ ✅, quickstart.md ✅

**Stack**: .NET 10 Isolated Worker · MediatR 12.x · CsvHelper 33.x · Parquet.Net 4.23.x · Azure.Storage.Files.DataLake 12.18.x · Azure.Storage.Blobs 12.x · Azure.Messaging.ServiceBus 7.18.x · OpenTelemetry 1.10.x · Microsoft.Extensions.Resilience (Polly v8)

## Format: `[ID] [P?] [Story?] Description`

- **[P]**: Can run in parallel (different files, no blocking dependencies)
- **[Story]**: User story this task belongs to — [US1], [US2], [US3]
- Exact file paths included in all descriptions

---

## Phase 1: Setup (Local Dev Infrastructure)

**Purpose**: Docker-based local environment for Service Bus and storage emulation. No existing code touched.

- [X] T001 Create `docker-compose.yml` with Azurite (ports 10000/10001/10002), Microsoft Service Bus Emulator (ports 5672/5300), and SQL Server 2022; define `sb-emulator` network; wire `depends_on` between emulator and SQL Server
- [X] T002 [P] Create `.env` with `CONFIG_PATH=./emulator/Config.json`, `ACCEPT_EULA=Y`, `MSSQL_SA_PASSWORD`; add `.env` to `.gitignore`
- [X] T003 [P] Create `emulator/Config.json` with namespace name `sbemulatorns` and two queues: `raw-energy-events` and `dataset-bronze-available`; set `MaxDeliveryCount: 3`, `LockDuration: PT1M` on both queues

**Checkpoint**: `docker compose up -d` starts all three containers without errors

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Fix all Constitution and spec violations in existing code before any user story work begins.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [X] T004 Audit `src/DatasetProcessingFunction/DatasetProcessingFunction.csproj` and remove any of these packages if present: `Azure.Monitor.OpenTelemetry.AspNetCore`, `Microsoft.ApplicationInsights.WorkerService`, `Microsoft.Azure.Functions.Worker.ApplicationInsights` — they conflict with the OTel path (Constitution VIII/IX)
- [X] T005 Rewrite `src/DatasetProcessingFunction/Program.cs`: (a) replace any App Insights worker service registration with `.AddOpenTelemetry().UseFunctionsWorkerDefaults().UseAzureMonitorExporter()`; (b) add `builder.Logging.AddOpenTelemetry(b => b.IncludeScopes = true)`; (c) remove the App Insights `Warning+` log-filter rule via `LoggerFilterOptions`; (d) add dual-mode `ServiceBusClient` construction — SAS+`UseDevelopmentEmulator=true` for local, `DefaultAzureCredential` + fully-qualified namespace for production (see research.md §2)
- [X] T006 Delete `src/DatasetProcessingFunction.Infrastructure/Telemetry/DatasetActivitySource.cs` (duplicate source); update the `.AddSource(...)` call in `Program.cs` to reference the single `DatasetActivitySource.Source` defined in the Domain layer (FR-023)
- [X] T007 Update `src/DatasetProcessingFunction/Functions/ProcessDatasetFunction.cs`: change `[ServiceBusTrigger]` attribute to use `"%SERVICEBUS_QUEUE_NAME%"` as the queue name with `Connection = "ServiceBusConnection"`; remove any `subscriptionName` argument (FR-001a)
- [X] T008 [P] Update `src/DatasetProcessingFunction.Domain/Models/VendorSchemaMapping.cs`: add `MinValue` (`decimal?`) and `MaxValue` (`decimal?`) properties to `ColumnMapping`; add guard that throws `ArgumentException` if both are non-null and `MinValue > MaxValue` (FR-010)
- [X] T009 [P] Update `src/DatasetProcessingFunction/local.settings.json`: set `SERVICEBUS_QUEUE_NAME=raw-energy-events` and `SERVICEBUS_BRONZE_QUEUE_NAME=dataset-bronze-available`; remove any topic/subscription settings; ensure `ServiceBusConnection` uses emulator connection string with `UseDevelopmentEmulator=true` (FR-001a, FR-025)
- [X] T010 [P] Configure `src/DatasetProcessingFunction/host.json` Service Bus extension block: `autoComplete: false`, `prefetchCount: 16`, `maxConcurrentCalls: 16`, `maxAutoLockRenewalDuration: "00:05:00"` (Constitution VIII)
- [X] T011 Update `src/DatasetProcessingFunction/Functions/ProcessDatasetFunction.cs`: add `correlation_id` fallback — after deserialising `DatasetAvailableEvent`, if `CorrelationId` is null or whitespace generate `Guid.NewGuid().ToString()`, log a structured warning (`"CorrelationId absent from event; generated fallback {CorrelationId}"`), and continue processing; do not dead-letter (FR-003)

**Checkpoint**: `dotnet build` passes with zero warnings (`TreatWarningsAsErrors` enabled); `func start` binds to the emulator `raw-energy-events` queue

---

## Phase 3: User Story 1 — Successful End-to-End Processing (Priority: P1) 🎯 MVP

**Goal**: A well-formed `dataset.available` event results in all CSV records being parsed, validated, transformed, enriched, written as Parquet to the Bronze layer, and a `dataset.bronze.available` event published — with full idempotency on re-delivery.

**Independent Test**: Seed `vendor-abc-v2-schema.json` in Azurite blob storage and `vendor-abc-v2-valid.csv` in ADLS; publish a `dataset.available` event to the `raw-energy-events` emulator queue; assert that a Parquet file appears at the expected Bronze path containing all vendor canonical columns plus the 5 enrichment columns; re-publish the same event and assert the partition is replaced (not duplicated).

### Tests for User Story 1

> **Write these tests FIRST — confirm they FAIL before writing any implementation code (Constitution VII — Red-Green-Refactor)**

- [X] T023 [P] [US1] Write unit tests in `tests/DatasetProcessingFunction.UnitTests/Infrastructure/OneLakeBronzeWriterTests.cs`: verify dynamic Parquet schema contains 5 enrichment columns first then all vendor columns; verify `datetime` fields use `DateTimeDataField`; verify `decimal` fields use `DecimalDataField(precision:18, scale:6)`; verify vendor columns that duplicate enrichment names are skipped (FR-017b)
- [X] T024 [P] [US1] Write unit tests in `tests/DatasetProcessingFunction.UnitTests/Domain/SchemaTransformerTests.cs`: `kw_to_w` multiplies by 1 000; `mw_to_w` by 1 000 000; `kwh_to_wh` by 1 000; `mwh_to_wh` by 1 000 000; null/empty/unrecognised key = pass-through (FR-015)
- [X] T025 [P] [US1] Write unit tests in `tests/DatasetProcessingFunction.UnitTests/Application/ProcessDatasetCommandHandlerTests.cs`: `WriteAsync` is called with the `VendorSchemaMapping` instance; `IProcessingMetricsEmitter.Emit` is called with correct counts; `IEventPublisher.PublishAsync` is called after a successful write; `IEventPublisher.PublishAsync` is NOT called when `WriteAsync` throws (FR-017b, FR-021, FR-026)
- [X] T026 [US1] Write integration tests in `tests/DatasetProcessingFunction.IntegrationTests/ProcessDatasetEndToEndTests.cs` (happy path): seed `vendor-abc-v2-schema.json` in Azurite blob container `schema-registry`; seed `vendor-abc-v2-valid.csv` in Azurite ADLS; publish `dataset.available` event to emulator queue; assert Bronze Parquet file exists at expected partition path; assert all canonical vendor columns + 5 enrichment columns are present; record wall-clock time from event publish to Bronze blob appearance and assert `< 60 000 ms`; re-publish the same event and assert the partition is not duplicated (SC-001, SC-002, SC-006)
- [X] T027 [P] [US1] Write unit test in `tests/DatasetProcessingFunction.UnitTests/Application/ProcessDatasetCommandHandlerTests.cs`: when `DatasetAvailableEvent.CorrelationId` is null, a new Guid is generated and assigned; a structured warning is logged; processing dispatches the command normally (FR-003)
- [ ] T056 [US1] Write a concurrency integration test in `tests/DatasetProcessingFunction.IntegrationTests/ProcessDatasetEndToEndTests.cs`: seed 50 distinct `vendor-abc-v2-valid.csv` fixtures (different `dataset_id` values) in Azurite; publish all 50 `dataset.available` events simultaneously via `Task.WhenAll`; assert all 50 Bronze Parquet partitions exist; assert zero messages on the DLQ (SC-005)

### Implementation for User Story 1

- [X] T012 Update `src/DatasetProcessingFunction.Application/Interfaces/IBronzeWriter.cs`: change `WriteAsync` signature to `WriteAsync(DatasetId datasetId, DateOnly ingestionDate, IReadOnlyList<CanonicalRecord> records, VendorSchemaMapping mapping, CancellationToken ct)` (FR-017b)
- [X] T013 Rewrite `src/DatasetProcessingFunction.Infrastructure/Storage/OneLakeBronzeWriter.cs`: build `ParquetSchema` dynamically from `VendorSchemaMapping.ColumnMappings` using the field-creation pattern in research.md §1 (enrichment columns first, then vendor columns skipping enrichment names); call `UploadAsync(overwrite: true)` for atomic idempotent replace (FR-017b, FR-018)
- [X] T053 [P] [US1] Update `src/DatasetProcessingFunction.Domain/Services/CsvParserService.cs` to read `VendorSchemaMapping.Delimiter` and configure `CsvHelper`'s `CsvConfiguration` with that delimiter character; supported values: `,` (comma), `;` (semicolon), `\t` (tab); pass the mapping (or delimiter char) as a parameter to the parse method — do not hard-code a comma delimiter (FR-006a)
- [X] T014 [P] [US1] Implement unit conversions in `src/DatasetProcessingFunction.Domain/Services/SchemaTransformer.cs`: apply `kw_to_w` (×1 000), `mw_to_w` (×1 000 000), `kwh_to_wh` (×1 000), `mwh_to_wh` (×1 000 000); pass through unchanged if key is null, empty, or unrecognised (FR-015)
- [X] T015 [P] [US1] Update `src/DatasetProcessingFunction.Domain/Services/RecordEnricher.cs`: read `site_id` value from `CanonicalRecord.Fields["site_id"]` — the value is placed there by `SchemaTransformer` via the `canonical_field: "site_id"` mapping; promote it to the `CanonicalRecord.SiteId` property (FR-016)
- [X] T016 [US1] Add schema-level invariant validation to `src/DatasetProcessingFunction.Infrastructure/SchemaRegistry/BlobSchemaRegistry.cs`: after deserialising `VendorSchemaMapping`, throw `UnknownSchemaException(vendorId, schemaVersion)` if no `ColumnMapping` has `CanonicalField == "site_id"` (FR-016) **and** throw if no `ColumnMapping` has `CanonicalField == "timestamp"` (FR-016a); both checks must run before the mapping is returned
- [X] T017 [US1] Register Polly `"adls-read"` resilience pipeline in `src/DatasetProcessingFunction/Program.cs` (3 total attempts = `MaxRetryAttempts: 2`; 2 s base; 30 s max; exponential; jitter; handle `IOException` and `RequestFailedException` with status 429/500/503/408 as transient); disable Azure SDK built-in retry by setting `DataLakeClientOptions.Retry.MaxRetries = 0`; wrap `AdlsDatasetReader.ReadAsync` to execute inside the pipeline (FR-005, research.md §3)
- [X] T018 [US1] Register Polly `"schema-registry-read"` resilience pipeline in `src/DatasetProcessingFunction/Program.cs` (same parameters as `"adls-read"`; 404 is NOT in `ShouldHandle` — it propagates immediately as `UnknownSchemaException`); wrap `BlobSchemaRegistry.GetMappingAsync` to execute inside the pipeline (FR-013a, research.md §4)
- [X] T019 [US1] Register `RecordEnricher` in the DI container in `src/DatasetProcessingFunction/Program.cs`; remove any `new RecordEnricher()` direct instantiation from `ProcessDatasetCommandHandler` (Constitution II — no direct handler instantiation)
- [X] T020 [US1] Update `src/DatasetProcessingFunction.Application/Commands/ProcessDatasetCommandHandler.cs`: inject `IProcessingMetricsEmitter`; pass `mapping` as explicit parameter to `WriteAsync` (not injected at construction time); call `Emit(...)` with final record counts and duration after the Bronze write completes (FR-017b, FR-021)
- [X] T021 [P] [US1] Verify `src/DatasetProcessingFunction.Infrastructure/Messaging/ServiceBusEventPublisher.cs` sends `DatasetBronzeAvailableEvent` to the queue named by `SERVICEBUS_BRONZE_QUEUE_NAME` setting with snake_case JSON fields: `dataset_id`, `record_count`, `bronze_path`, `schema_version`, `correlation_id`, `published_at` (FR-025)
- [X] T022 [P] [US1] Create `src/DatasetProcessingFunction/Functions/WarmupFunction.cs` with a `[WarmupTrigger]`-decorated `RunAsync` method that resolves key DI singletons (`DataLakeServiceClient`, `ServiceBusClient`, `ISchemaRegistry`) to pre-load them on scale-out (Constitution VIII)

**Checkpoint**: End-to-end integration test passes; Bronze Parquet contains all canonical columns; re-delivery produces identical output (idempotency verified)

---

## Phase 4: User Story 2 — Data Quality Failure Handling (Priority: P2)

**Goal**: All invalid datasets (missing timestamp, out-of-range values, unknown schema, empty file, non-UTF-8 encoding) are detected, routed to the dead-letter queue with structured payloads, and produce zero Bronze records.

**Independent Test**: For each failure type, seed the corresponding fixture CSV in Azurite and publish a `dataset.available` event; assert the message appears on the DLQ with the correct `error_type`; assert no Parquet file was created in the Bronze layer.

### Tests for User Story 2

> **Write these tests FIRST — confirm they FAIL before writing any implementation code (Constitution VII — Red-Green-Refactor)**

- [X] T033 [P] [US2] Write unit tests in `tests/DatasetProcessingFunction.UnitTests/Domain/DataQualityValidatorTests.cs`: value below `MinValue` → `NumericRange` failure; value above `MaxValue` → `NumericRange` failure; value within bounds → pass; null `MinValue` and null `MaxValue` → no range check (FR-010)
- [X] T054 [P] [US2] Write unit tests for timestamp validation in `tests/DatasetProcessingFunction.UnitTests/Domain/DataQualityValidatorTests.cs`: record with null `timestamp` canonical field value → `ValidationFailure` with `Rule = "RequiredField"`; record with non-parseable string in `timestamp` field → `ValidationFailure` with `Rule = "NumericParse"`; record with valid ISO datetime string → passes (FR-009)
- [X] T034 [P] [US2] Write unit tests in `tests/DatasetProcessingFunction.UnitTests/Domain/CsvParserServiceTests.cs`: header-only CSV → `EmptyDatasetException`; CSV bytes with non-UTF-8 encoding → `UnsupportedEncodingException`; CSV with mixed `\r\n`/`\n` line endings → parsed successfully, no exception (FR-006b, FR-006c)
- [ ] T035 [US2] Write integration test in `tests/DatasetProcessingFunction.IntegrationTests/ProcessDatasetEndToEndTests.cs`: seed `vendor-abc-v2-empty.csv` (header only); assert message lands on DLQ with `error_type: "EmptyDataset"`; assert no Bronze blob created (FR-006c, SC-003)
- [ ] T036 [US2] Write integration test in `tests/DatasetProcessingFunction.IntegrationTests/ProcessDatasetEndToEndTests.cs`: seed `vendor-unknown-schema.csv` (unrecognised header); assert DLQ message has `error_type: "UnknownSchema"` with correct `vendor_id` and `schema_version`; assert no Bronze blob (FR-011, US2-S3)
- [X] T037 [P] [US2] Write unit test in `tests/DatasetProcessingFunction.UnitTests/Infrastructure/BlobSchemaRegistryTests.cs` (new file): schema JSON with no `ColumnMapping` having `canonical_field: "timestamp"` → `BlobSchemaRegistry.GetMappingAsync` throws `UnknownSchemaException`; dead-letter catch in `ProcessDatasetFunction` maps it to `error_type: "UnknownSchema"` (FR-016a)

### Implementation for User Story 2

- [X] T028 [P] [US2] Create all three domain exception classes in `src/DatasetProcessingFunction.Domain/Exceptions/`: (a) `UnknownSchemaException.cs` with `VendorId` and `SchemaVersion` string properties; (b) `UnsupportedEncodingException.cs` with `DetectedEncoding` string property; (c) `DatasetValidationException.cs` with a `ValidationResult` property — these must exist before the catch blocks in T032 can compile (FR-011, FR-006b)
- [X] T029 [US2] Add empty-dataset detection to `src/DatasetProcessingFunction.Domain/Services/CsvParserService.cs`: after reading and validating the header row, if the CSV contains zero data rows throw `EmptyDatasetException`; the header row must still be parsed (FR-006c)
- [X] T030 [US2] Add UTF-8 encoding check to `src/DatasetProcessingFunction.Domain/Services/CsvParserService.cs`: before parsing, read the first bytes to detect BOM or use `Ude` / charset detection; if encoding is not UTF-8 throw `UnsupportedEncodingException(detectedEncoding)` with the detected encoding name in the message (FR-006b)
- [X] T055 [US2] Add per-record timestamp validation to `src/DatasetProcessingFunction.Domain/Services/DataQualityValidator.cs`: for each record, verify the value in the `timestamp` canonical field (after schema transformation) is non-null and parseable as a valid `DateTimeOffset`; add a `ValidationFailure` with `Rule = "RequiredField"` for null and `Rule = "NumericParse"` for unparseable values (FR-009)
- [X] T031 [P] [US2] Add numeric range-boundary validation to `src/DatasetProcessingFunction.Domain/Services/DataQualityValidator.cs`: for each `ColumnMapping` where `DataType` is `"decimal"`, `"double"`, or `"float"` and `MinValue` or `MaxValue` is non-null, validate the parsed value against the bounds; add a `ValidationFailure` with `Rule = "NumericRange"` for each out-of-bounds record (FR-010)
- [X] T032 [US2] Update `src/DatasetProcessingFunction/Functions/ProcessDatasetFunction.cs` dead-letter catch blocks to handle: `EmptyDatasetException` → `error_type: "EmptyDataset"`; `UnsupportedEncodingException` → `error_type: "UnsupportedEncoding"` with `detected_encoding`; `DatasetValidationException` → `error_type: "ValidationFailed"` with `fail_count` and first 10 `failures`; `UnknownSchemaException` → `error_type: "UnknownSchema"` with `vendor_id` and `schema_version`; all payloads must conform to `contracts/dead-letter-reason.json` (FR-003, FR-006b, FR-006c, FR-011, FR-019)

**Checkpoint**: All five dead-letter paths (EmptyDataset, UnsupportedEncoding, ValidationFailed, UnknownSchema from missing site_id, UnknownSchema from missing timestamp) produce correct structured DLQ payloads; Bronze layer is empty for every failure scenario

---

## Phase 5: User Story 3 — Observability and Metrics Emission (Priority: P3)

**Goal**: Every execution (success and failure) emits correlated distributed-trace spans, custom OTel metrics, and structured log entries with `CorrelationId` — all observable without raw function logs.

**Independent Test**: Process a batch of synthetic `dataset.available` events against the emulator; query the OTel output (OTLP or console exporter in test mode) for `dataset.csv.parse`, `dataset.validation.run`, and `dataset.bronze.write` child spans; verify `records_processed` counter increments; verify all log entries share the same `CorrelationId`.

### Tests for User Story 3

> **Write these tests FIRST — confirm they FAIL before writing any implementation code (Constitution VII — Red-Green-Refactor)**

- [X] T041 [P] [US3] Write unit tests in `tests/DatasetProcessingFunction.UnitTests/Infrastructure/ProcessingMetricsEmitterTests.cs`: calling `Emit(datasetId, recordCount, passCount, failCount, durationMs)` invokes all 4 instruments with correct values; `dataset_id` tag matches the supplied value (FR-021)
- [X] T042 [P] [US3] Write unit test in `tests/DatasetProcessingFunction.UnitTests/Application/ProcessDatasetCommandHandlerTests.cs`: the `CorrelationId` from the dispatched command appears as an attribute on the active `Activity` span after handler execution (FR-020)

### Implementation for User Story 3

- [X] T038 [US3] Add named OTel `Activity` spans to domain services — all using `DatasetActivitySource.Source` from `src/DatasetProcessingFunction.Domain/Telemetry/DatasetActivitySource.cs`: (a) `dataset.csv.parse` in `CsvParserService`; (b) `dataset.validation.run` in `DataQualityValidator`; (c) `dataset.bronze.write` in `OneLakeBronzeWriter`; add `dataset_id` as a span tag on each (FR-023)
- [X] T039 [P] [US3] Verify `src/DatasetProcessingFunction.Infrastructure/Telemetry/ProcessingMetricsEmitter.cs` declares: `Counter<long>` named `records_processed`; `Counter<long>` named `validation_pass_count`; `Counter<long>` named `validation_fail_count`; `Histogram<long>` named `processing_duration_ms`; all instruments tagged with `dataset_id` on each `Add`/`Record` call (FR-021)
- [X] T040 [US3] Propagate `CorrelationId` as structured context in `src/DatasetProcessingFunction/Functions/ProcessDatasetFunction.cs` and `src/DatasetProcessingFunction.Application/Commands/ProcessDatasetCommandHandler.cs`: push `CorrelationId` into `ILogger` scope (`using (_logger.BeginScope(...))`); add as OTel span attribute `correlation_id` on the active `Activity`; ensure the generated Guid fallback (T011) is also propagated through the same path (FR-020)

**Checkpoint**: Named child spans (`dataset.csv.parse`, `dataset.validation.run`, `dataset.bronze.write`) visible in OTel output; `records_processed` counter increments on each successful run; all log entries correlated by `CorrelationId`

---

## Phase 6: CI/CD Pipeline

**Purpose**: Enforce all CI/CD gates mandated by Constitution §8.

- [ ] T043 Extend `.github/workflows/ci-cd.yml` with sequential stages in order: **Build** (`dotnet build --configuration Release` with `TreatWarningsAsErrors`) → **Unit Tests** (`dotnet test` on UnitTests project; enforce ≥ 80% branch coverage on Domain + Application layers via `coverlet` report) → **Integration Tests** (`docker compose up -d` + `dotnet test` on IntegrationTests project) → **Bicep What-If** (`az deployment group what-if`; attach output as PR artefact) → **Deploy to Staging Slot** (`AzureFunctionApp@2` with `deployToSlotOrASE: true`) → **Health Check** (availability probe on staging slot URL) → **Slot Swap** (`AzureAppServiceManage@0`; conditional on health check pass); authenticate all Azure steps via Workload Identity Federation (`azure/login@v2` with `permissions: id-token: write`) (FR-024, Constitution §8)
- [ ] T044 [P] Add post-deploy observability validation step to `.github/workflows/ci-cd.yml`: after slot swap, query App Insights REST API for at least one trace tagged `cloud_RoleName=dataset-processing-func` (matching `OTEL_SERVICE_NAME`) within the last 30 seconds; emit a warning (non-blocking) if no trace found within 2 minutes (SC-004, Constitution §8)

---

## Phase 7: Bicep Infrastructure

**Purpose**: Declare all Azure resources as code (Constitution IV).

- [ ] T045 [P] Create `infrastructure/modules/functionApp.bicep`: Function App on Premium EP1 plan (Windows); `WEBSITE_RUN_FROM_PACKAGE=1`; all sensitive app settings as Key Vault references (`@Microsoft.KeyVault(...)`); `OTEL_SERVICE_NAME=dataset-processing-func`; `APPLICATIONINSIGHTS_CONNECTION_STRING` reference; `SERVICEBUS_QUEUE_NAME` and `SERVICEBUS_BRONZE_QUEUE_NAME`; deployment slots: `staging` (Constitution IV, VIII)
- [ ] T046 [P] Create `infrastructure/modules/serviceBus.bicep`: Service Bus namespace (Basic tier); `raw-energy-events` queue with `maxDeliveryCount: 3`, `lockDuration: PT1M`; `dataset-bronze-available` queue with same settings; DLQ depth alert (`> 0`) wired to App Insights (Constitution IV, V, FR-001)
- [ ] T047 [P] Create `infrastructure/modules/storage.bicep`: ADLS Gen2 storage account (HNS enabled) for Bronze layer with dedicated container `bronze`; separate Blob storage account for schema registry with container `schema-registry`; neither account is `AzureWebJobsStorage` (Constitution IV, VIII, FR-017a)
- [ ] T048 [P] Create `infrastructure/modules/appInsights.bicep`: Application Insights workspace-based resource; error-rate alert (`exceptions/count > 0` over 5 min); availability test targeting the staging slot health endpoint (Constitution IV, V)
- [ ] T049 [P] Create `infrastructure/modules/keyVault.bicep`: Key Vault; RBAC assignment `Key Vault Secrets User` for the Function App system-assigned managed identity; store `ServiceBusConnection` (production fully-qualified namespace), `ADLS_ENDPOINT`, `SCHEMA_REGISTRY_ENDPOINT` as secrets (Constitution IV, VI)
- [ ] T050 Create `infrastructure/main.bicep` that orchestrates all five modules with parameter passing; create `infrastructure/main.parameters.json` for environment-specific values; update `azure.yaml` to point `azd up` at `infrastructure/main.bicep` (Constitution IV)

---

## Phase 8: Polish & Cross-Cutting Concerns

- [ ] T051 Scan all files under `src/` for `Console.WriteLine` and `Debug.WriteLine` calls; replace each with the injected `ILogger<T>` equivalent using structured log properties (FR-022)
- [ ] T052 Validate `specs/002-dataset-ingestion-pipeline/quickstart.md` end-to-end: run `docker compose up -d`; seed `vendor-abc-v2-schema.json` and `vendor-abc-v2-valid.csv` into Azurite using the Azure Storage Explorer or `az storage` CLI commands documented in quickstart.md; start the function with `func start`; publish a `dataset.available` event; confirm Bronze Parquet appears at the expected path and a `dataset.bronze.available` message arrives on the outbound queue (SC-001)

---

## Dependencies

```text
Phase 1 (T001–T003)
    └─► Phase 2 (T004–T011)  [BLOCKING — must complete before any story work]
            ├─► Phase 3 / US1 (T012–T027)  [P1 — MVP]
            │       └─► Phase 4 / US2 (T028–T037)  [P2 — can start after T006, T008]
            │               └─► Phase 5 / US3 (T038–T042)  [P3 — spans added after services exist]
            ├─► Phase 6 / CI/CD (T043–T044)  [independent of story phases; parallel with Phase 3+]
            └─► Phase 7 / Bicep (T045–T050)  [independent; parallel with Phase 3+]

Phase 8 (T051–T052) — runs after all phases complete
```

**Within-phase parallelism:**

- Phase 1: T002 and T003 are parallel with T001
- Phase 2: T008, T009, T010 are parallel with each other (different files)
- Phase 3 US1: T014, T015, T021, T022, T023, T024, T025, T027 are parallel; T016–T020 are sequential
- Phase 4 US2: T028, T031, T033, T034, T037 are parallel; T029→T030→T032 are sequential
- Phase 5 US3: T039, T041, T042 are parallel; T038 and T040 are sequential
- Phase 7: T045–T049 are all parallel; T050 waits for all five

---

## Implementation Strategy

**MVP Scope (Phase 1 + Phase 2 + Phase 3)**: After completing T001–T027, T053, T056, User Story 1 is fully functional and independently testable end-to-end against the local emulator. This is the recommended first delivery increment.

**Increment 2 (Phase 4)**: Add all failure-handling and dead-letter paths (T028–T037, T054, T055). No changes to the happy path.

**Increment 3 (Phase 5)**: Layer in OTel spans and correlated metrics (T038–T042). No functional changes.

**Increment 4 (Phases 6–7)**: CI/CD pipeline gates and Bicep IaC — required for production slot swap. Can be developed in parallel with increments 2–3.

**Increment 5 (Phase 8)**: Polish and quickstart validation (T051–T052). Can be done at any point after Increment 1.

---

## Summary

| Phase | Story | Tasks | Parallel Opportunities |
| --- | --- | --- | --- |
| 1 — Setup | — | T001–T003 | T002, T003 ∥ T001 |
| 2 — Foundational | — | T004–T011 | T008, T009, T010 ∥ each other |
| 3 — US1 (P1 MVP) | US1 | T012–T027, T053, T056 | 9 parallel tasks |
| 4 — US2 (P2) | US2 | T028–T037, T054, T055 | 6 parallel tasks |
| 5 — US3 (P3) | US3 | T038–T042 | T039, T041, T042 ∥ |
| 6 — CI/CD | — | T043–T044 | T044 ∥ T043 |
| 7 — Bicep | — | T045–T050 | T045–T049 ∥ |
| 8 — Polish | — | T051–T052 | — |
| **Total** | | **56 tasks** | |
