# Tasks: Dataset Ingestion Pipeline (Raw → Bronze)

**Input**: Design documents from `specs/001-dataset-ingestion-pipeline/` and `specs/main/`
**Prerequisites**: plan.md ✅, spec.md ✅, research.md ✅, data-model.md ✅, contracts/ ✅, quickstart.md ✅

**Tests**: Included — SC-007 explicitly requires ≥ 80% unit test coverage on Domain and Application layers; FR-024 mandates coverage gate in CI/CD.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story?] Description`

- **[P]**: Can run in parallel (different files, no incomplete-task dependencies)
- **[Story]**: Which user story this task belongs to (US1, US2, US3)
- Exact file paths are included in every description

---

## Phase 1: Setup (Local Dev Infrastructure)

**Purpose**: Create the Docker-based local environment (Azurite + Service Bus Emulator) so integration tests and `func start` work without cloud dependency. These are **new files** only — no existing code is touched.

- [x] T001 Create `docker-compose.yml` at service root with three services: `azurite` (ports 10000/10001/10002), `emulator` (`mcr.microsoft.com/azure-messaging/servicebus-emulator:latest`, port 5672/5300, mounts Config.json), `mssql` (`mcr.microsoft.com/mssql/server:2022-latest`) — with `sb-emulator` network and `depends_on` wiring per research.md §2
- [x] T002 [P] Create `.env` at service root with `CONFIG_PATH`, `ACCEPT_EULA=Y`, `MSSQL_SA_PASSWORD=YourStr0ng!Pass`; add `.env` to `.gitignore`
- [x] T003 [P] Create `emulator/Config.json` with `sbemulatorns` namespace (fixed name — any other name silently fails), `raw-energy-events` queue and `dataset-bronze-available` queue, `MaxDeliveryCount: 3`, `LockDuration: PT1M`

**Checkpoint**: `docker compose up -d` starts all three containers cleanly

---

## Phase 2: Foundational (Constitution & Spec Violations — Blocking Prerequisites)

**Purpose**: Fix bugs in the existing skeleton that block correct operation of every user story. No user story can be verified until these are done.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [x] T004 [P] Remove `Azure.Monitor.OpenTelemetry.AspNetCore`, `Microsoft.ApplicationInsights.WorkerService`, and `Microsoft.Azure.Functions.Worker.ApplicationInsights` `<PackageReference>` entries from `src/DatasetProcessingFunction/DatasetProcessingFunction.csproj` (Constitution VIII/IX violations — these co-exist with the OTel exporter path and duplicate request spans)
- [x] T005 [P] Add `<PackageReference Include="Microsoft.Extensions.Resilience" Version="10.*" />` to `src/DatasetProcessingFunction.Infrastructure/DatasetProcessingFunction.Infrastructure.csproj` (required for Polly v8 pipeline — FR-005)
- [x] T006 [P] Add `public decimal? MinValue { get; init; }` and `public decimal? MaxValue { get; init; }` properties to the `ColumnMapping` record in `src/DatasetProcessingFunction.Domain/Models/VendorSchemaMapping.cs`; add guard: if both are non-null, `MinValue <= MaxValue` must hold (FR-010)
- [x] T007 Fix `src/DatasetProcessingFunction/Program.cs`: (a) replace `Azure.Monitor.OpenTelemetry.AspNetCore` distro registration with `Azure.Monitor.OpenTelemetry.Exporter`-only wiring; (b) add `builder.Services.Configure<LoggerFilterOptions>(opts => opts.Rules.Clear())` to remove the App Insights `Warning+` filter so all log levels flow (Constitution VIII); (c) add dual-mode `ServiceBusClient` construction — detect `UseDevelopmentEmulator` in `ServiceBusConnection` and use SAS string, otherwise use `fullyQualifiedNamespace` + `DefaultAzureCredential` (research.md §2)
- [x] T008 Delete `src/DatasetProcessingFunction.Infrastructure/Telemetry/DatasetActivitySource.cs` (duplicate — FR-023 violation); update `Program.cs` OTel `.AddSource(...)` call to reference `DatasetProcessingFunction.Domain.Telemetry.DatasetActivitySource.Name` so the single Domain-layer source is registered
- [x] T009 Replace the topic+subscription `[ServiceBusTrigger]` attribute in `src/DatasetProcessingFunction/Functions/ProcessDatasetFunction.cs` with a queue-based trigger: `[ServiceBusTrigger("%SERVICEBUS_QUEUE_NAME%", Connection = "ServiceBusConnection")]`; remove any `subscriptionName` argument; default queue name is `raw-energy-events` (FR-001a)
- [x] T010 [P] Update `src/DatasetProcessingFunction/local.settings.json` with all settings from quickstart.md: `ServiceBusConnection` (SAS string with `UseDevelopmentEmulator=true`), `SERVICEBUS_QUEUE_NAME` (value: `raw-energy-events`), `SERVICEBUS_BRONZE_QUEUE_NAME` (value: `dataset-bronze-available`), `ADLS_ENDPOINT`, `ONELAKE_ENDPOINT`, `SCHEMA_REGISTRY_BLOB_CONNECTION`, `SCHEMA_REGISTRY_CONTAINER`, `BRONZE_FILESYSTEM`, `OTEL_SERVICE_NAME`; remove `ServiceBusConnection__fullyQualifiedNamespace` and any `SERVICEBUS_TOPIC_NAME` / `SERVICEBUS_SUBSCRIPTION_NAME` entries if present

**Checkpoint**: `dotnet build` passes with zero errors and zero warnings (`TreatWarningsAsErrors` is enabled); `func start` binds to emulator successfully after `docker compose up`

---

## Phase 3: User Story 1 — Successful End-to-End Dataset Processing (Priority: P1) 🎯 MVP

**Goal**: A well-formed `dataset.available` event triggers the full pipeline — CSV retrieval → parse → validate → transform → enrich → write all canonical columns to Bronze Parquet → publish `dataset.bronze.available`.

**Independent Test**: Seed Azurite with `vendor-abc-v2-schema.json` and `vendor-abc-v2-valid.csv`; publish a `dataset.available` event to the emulator; assert Bronze Parquet exists at the expected path and contains all mapped canonical columns (not only the 5 enrichment columns).

### Implementation for User Story 1

- [x] T011 [P] [US1] Update `src/DatasetProcessingFunction.Application/Interfaces/IBronzeWriter.cs` — change `WriteAsync` signature to `WriteAsync(DatasetId id, DateOnly date, IReadOnlyList<CanonicalRecord> records, VendorSchemaMapping mapping, CancellationToken ct)` (FR-017b)
- [x] T012 [US1] Rewrite `src/DatasetProcessingFunction.Infrastructure/Storage/OneLakeBronzeWriter.cs` — replace the 5-fixed-column Parquet write with a dynamic schema built from `mapping.ColumnMappings` using `DateTimeDataField` (INT64 millis, not INT96), `DecimalDataField(name, 18, 6, isNullable:true)`, and typed `DataField` for other types; prepend the 5 fixed enrichment columns; skip vendor columns that duplicate enrichment names; extract typed `DataColumn` arrays in `schema.DataFields` order per `BuildColumnArray` pattern in research.md §1; use `DataLakeFileClient.UploadAsync(overwrite: true)` to atomically replace any existing partition file at the same path — never append (FR-017b, FR-018)
- [ ] T012b [P] [US1] Implement unit conversion in `src/DatasetProcessingFunction.Domain/Services/SchemaTransformer.cs` — for each `ColumnMapping` where `UnitConversion` is non-null, apply the defined conversion before writing to `CanonicalRecord.Fields`: `"kw_to_w"` (× 1000), `"mw_to_w"` (× 1,000,000), `"kwh_to_wh"` (× 1000), `"mwh_to_wh"` (× 1,000,000); fields without a `UnitConversion` are passed through unchanged (FR-015)
- [ ] T012c [P] [US1] Add unit tests in `tests/DatasetProcessingFunction.UnitTests/Domain/SchemaTransformerTests.cs` — assert: `kw_to_w` multiplies value by 1000; `mw_to_w` multiplies by 1,000,000; null `UnitConversion` passes value through; unknown conversion key throws `ArgumentException` (FR-015)
- [x] T013 [P] [US1] In `src/DatasetProcessingFunction/Program.cs` register the Polly "adls-read" resilience pipeline via `builder.Services.AddResiliencePipeline("adls-read", ...)` with `MaxRetryAttempts=2`, `Exponential` backoff, `UseJitter=true`, `Delay=2s`, `MaxDelay=30s`, `ShouldHandle` for `IOException` and `RequestFailedException` with status 429/500/503/408; update `DataLakeServiceClient` registration to set `DataLakeClientOptions.Retry.MaxRetries = 0` (disables SDK built-in retry to prevent multiplicative attempts — research.md §3)
- [x] T014 [US1] Rewrite `src/DatasetProcessingFunction.Infrastructure/Storage/AdlsDatasetReader.cs` — inject `ResiliencePipelineProvider<string>` in constructor; resolve `_pipeline.GetPipeline("adls-read")`; wrap `fileClient.ReadAsync` inside `_pipeline.ExecuteAsync(async ct => { ... }, cancellationToken)` copying response to `MemoryStream`; remove any existing inline retry logic (FR-005)
- [x] T015 [P] [US1] In `src/DatasetProcessingFunction/Program.cs` add `builder.Services.AddSingleton<RecordEnricher>()` (or `AddTransient` per existing pattern); remove `new RecordEnricher()` instantiation from `src/DatasetProcessingFunction.Application/Commands/ProcessDatasetCommandHandler.cs` and inject via constructor (Constitution II — DI)
- [x] T016 [US1] Update `src/DatasetProcessingFunction.Application/Commands/ProcessDatasetCommandHandler.cs` — inject `ProcessingMetricsEmitter`; pass `mapping` as the new `VendorSchemaMapping` parameter to `_bronzeWriter.WriteAsync(...)`; call `_metricsEmitter.Emit(metrics)` after successful write (FR-017b, FR-021)
- [x] T017 [US1] Verify `src/DatasetProcessingFunction.Infrastructure/Messaging/ServiceBusEventPublisher.cs` publishes `dataset.bronze.available` with all required fields (`dataset_id`, `record_count`, `bronze_path`, `schema_version`, `correlation_id`) using snake_case JSON keys and `Subject: "dataset.bronze.available"` per contracts/dataset-bronze-available-event.json; confirm it is NOT called on failure paths (FR-025/FR-026)

### Tests for User Story 1

- [x] T018 [P] [US1] Add unit tests in `tests/DatasetProcessingFunction.UnitTests/Infrastructure/OneLakeBronzeWriterTests.cs` — assert: schema contains 5 enrichment columns + all vendor columns; `datetime` columns use `DateTimeDataField`; `decimal` columns use `DecimalDataField(precision:18, scale:6)`; vendor columns duplicating enrichment names are skipped; column count equals `mapping.ColumnMappings.Count + 5` (minus skipped duplicates)
- [x] T019 [P] [US1] Update `tests/DatasetProcessingFunction.UnitTests/Application/ProcessDatasetCommandHandlerTests.cs` — add tests asserting: `IBronzeWriter.WriteAsync` is called with `VendorSchemaMapping` argument; `ProcessingMetricsEmitter.Emit` is called with correct record counts; `IEventPublisher` is called with a `dataset.bronze.available` payload after successful write; `IEventPublisher` is NOT called when Bronze write throws
- [x] T020 [US1] Add integration test in `tests/DatasetProcessingFunction.IntegrationTests/ProcessDatasetEndToEndTests.cs` — seed `vendor-abc-v2-schema.json` + `vendor-abc-v2-valid.csv` fixture in Azurite; send `dataset.available` message to Service Bus emulator; assert Bronze Parquet blob exists at expected path and Parquet file contains columns for all `ColumnMappings` entries plus the 5 enrichment columns (SC-001, SC-006); then re-send the same `dataset.available` message and assert the Bronze blob contains exactly the same record count with no duplication — verifying atomic overwrite (FR-018, SC-002)

**Checkpoint**: `dotnet test tests/DatasetProcessingFunction.UnitTests/` green; end-to-end test publishes event and verifies Bronze Parquet contains all canonical columns

---

## Phase 4: User Story 2 — Data Quality Failure Handling (Priority: P2)

**Goal**: Malformed, empty, or out-of-range CSV files are detected early, produce structured dead-letter payloads, and leave the Bronze layer untouched.

**Independent Test**: Publish events referencing CSV fixtures for each failure mode (empty, missing timestamp, out-of-range values); assert Service Bus DLQ receives a message with the correct structured `error_type`; assert no Bronze blob is created.

### Implementation for User Story 2

- [ ] T020b [P] [US2] Create `src/DatasetProcessingFunction.Domain/Exceptions/UnknownSchemaException.cs` — exception class carrying `VendorId` (string) and `SchemaVersion` (string) properties; thrown by `BlobSchemaRegistry` (or the command handler) when `GetMappingAsync` returns null (FR-011)
- [x] T021 [P] [US2] Create `src/DatasetProcessingFunction.Domain/Exceptions/EmptyDatasetException.cs` — simple exception class with a message; no extra fields required (FR-006c)
- [x] T022 [P] [US2] Add empty-dataset detection to `src/DatasetProcessingFunction.Domain/Services/CsvParserService.cs` — after reading header, if no data rows exist, throw `EmptyDatasetException`; add UTF-8 encoding check (FR-006b/FR-006c)
- [x] T023 [P] [US2] Add numeric range-boundary check to `src/DatasetProcessingFunction.Domain/Services/DataQualityValidator.cs` — for each `ColumnMapping` where `DataType` is `decimal`/`double`/`float` and `MinValue` or `MaxValue` is non-null, compare the parsed record value against the boundary; add a `ValidationFailure` with rule `"NumericRange"` for each violation (FR-010)
- [x] T024 [US2] Update `src/DatasetProcessingFunction/Functions/ProcessDatasetFunction.cs` dead-letter catch blocks — catch `EmptyDatasetException` → dead-letter with `error_type: "EmptyDataset"`; catch `UnsupportedEncodingException` → dead-letter with `error_type: "UnsupportedEncoding"` and `detected_encoding`; catch `DatasetValidationException` → dead-letter with `error_type: "ValidationFailed"`, `fail_count`, and first 10 `failures` entries; catch `UnknownSchemaException` → dead-letter with `error_type: "UnknownSchema"`, `vendor_id`, and `schema_version`; all payloads must match `contracts/dead-letter-reason.json` schema (FR-003, FR-006b, FR-006c, FR-011, FR-019)

### Tests for User Story 2

- [x] T025 [P] [US2] Add unit tests in `tests/DatasetProcessingFunction.UnitTests/Domain/DataQualityValidatorTests.cs` — test: value below `MinValue` adds `NumericRange` failure; value above `MaxValue` adds `NumericRange` failure; value within bounds passes; `MinValue=null` and `MaxValue=null` skips range check; non-numeric fields are not range-checked
- [x] T026 [P] [US2] Add unit tests in `tests/DatasetProcessingFunction.UnitTests/Domain/CsvParserServiceTests.cs` — test: header-only CSV throws `EmptyDatasetException`; non-UTF-8 bytes throw `UnsupportedEncodingException`; mixed `\r\n`/`\n` line endings are normalised (no exception)
- [x] T027 [US2] Add fixture `tests/DatasetProcessingFunction.IntegrationTests/fixtures/vendor-abc-v2-empty.csv` (header row only, no data rows); add integration test asserting the message is dead-lettered with `error_type: "EmptyDataset"` and no Bronze blob is written (FR-006c, SC-003)
- [ ] T027b [US2] Add fixture `tests/DatasetProcessingFunction.IntegrationTests/fixtures/vendor-unknown-schema.csv` (header row with unrecognised vendor column names); add integration test asserting the message is dead-lettered with `error_type: "UnknownSchema"` containing `vendor_id` and `schema_version` from the exception, and no Bronze blob is written (FR-011, US2 Scenario 3)

**Checkpoint**: All 3 dead-letter paths (empty, encoding, validation) produce correct structured DLQ payloads; Bronze layer remains empty for all failure scenarios

---

## Phase 5: User Story 3 — Observability and Metrics Emission (Priority: P3)

**Goal**: Every execution emits correlated OTel traces with named child spans and custom metric instruments visible in Application Insights, tagged with `dataset_id` and `correlation_id`.

**Independent Test**: Process a synthetic event; query `customMetrics` in Application Insights (or the OTel SDK's in-memory exporter in unit tests) for `records_processed`, `validation_pass_count`, `validation_fail_count`, `processing_duration_ms` with `dataset_id` tag; verify `CorrelationId` appears in span attributes.

### Implementation for User Story 3

- [x] T028 [P] [US3] Add named `Activity` spans to domain service methods in `src/DatasetProcessingFunction.Domain/Services/CsvParserService.cs`, `src/DatasetProcessingFunction.Domain/Services/DataQualityValidator.cs`, and `src/DatasetProcessingFunction.Infrastructure/Storage/OneLakeBronzeWriter.cs` — start each span from `DatasetActivitySource.Source` (Domain layer only); name spans `"csv.parse"`, `"validation.run"`, `"bronze.write"` respectively; set `dataset_id` as span attribute (FR-023)
- [x] T029 [P] [US3] Verify `src/DatasetProcessingFunction.Infrastructure/Telemetry/ProcessingMetricsEmitter.cs` defines OTel `Counter` or `Histogram` instruments for `records_processed`, `validation_pass_count`, `validation_fail_count`, and `processing_duration_ms`; each instrument call MUST include `dataset_id` as a tag/dimension via `KeyValuePair<string, object?>` (FR-021)
- [x] T030 [US3] In `src/DatasetProcessingFunction/Functions/ProcessDatasetFunction.cs` and `src/DatasetProcessingFunction.Application/Commands/ProcessDatasetCommandHandler.cs`, propagate `CorrelationId` from `DatasetAvailableEvent` as a structured log scope property and as an OTel span attribute `correlation_id`; confirm it appears in every log entry in the execution path (FR-020)

### Tests for User Story 3

- [x] T031 [P] [US3] Create `tests/DatasetProcessingFunction.UnitTests/Infrastructure/ProcessingMetricsEmitterTests.cs` — use OTel SDK `MetricSnapshotReader` or a mock meter to assert all 4 instruments are invoked with correct values and `dataset_id` tag when `Emit(metrics)` is called
- [x] T032 [US3] Add test in `tests/DatasetProcessingFunction.UnitTests/Application/ProcessDatasetCommandHandlerTests.cs` — assert `CorrelationId` from `DatasetAvailableEvent` is passed as an attribute on the active `Activity` span when handler executes

**Checkpoint**: OTel traces visible in local Application Insights emulator with named child spans; `records_processed` metric counter increments on each successful processing run

---

## Phase 6: CI/CD Pipeline

**Purpose**: Automate build, test coverage gate, Bicep what-if, staging deploy, and slot swap per Constitution §8 and FR-024.

- [x] T033 Create `.github/workflows/deploy.yml` with sequential stages: **Build** (dotnet build, TreatWarningsAsErrors) → **Unit Tests** (dotnet test with XPlat Code Coverage; fail if Domain + Application layer coverage < 80%) → **Integration Tests** (requires docker compose up in the CI runner) → **Bicep What-If** (az deployment group what-if) → **Deploy to Staging Slot** (`AzureFunctionApp@2` with `deployToSlotOrASE: true`, slot `staging`) → **Health Check** (HTTP probe on staging slot URL) → **Slot Swap** (`AzureAppServiceManage@0` conditional on health check passage)
- [x] T034 [P] Add Workload Identity Federation (OIDC) service connection configuration to the workflow — use `azure/login@v2` with `client-id`, `tenant-id`, `subscription-id` from GitHub secrets (no long-lived `clientSecret`); remove any legacy `creds` JSON secret
- [x] T035 [P] Add post-deploy observability validation step in the workflow — query Application Insights via `az monitor app-insights query` for a trace tagged `OTEL_SERVICE_NAME=dataset-processing-func` within the last 5 minutes; fail the stage if no trace is found

**Checkpoint**: Push to `main` triggers the full pipeline; test coverage gate blocks merge if below 80%

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Final correctness checks across all stories

- [x] T036 [P] Scan all `src/` production code files for `Console.WriteLine`, `Debug.WriteLine`, and unstructured `Console.Write` calls; replace any found with appropriate `ILogger` calls (FR-022)
- [x] T037 Run quickstart.md end-to-end validation: `docker compose up -d`, seed schema registry blob, seed `vendor-abc-v2-valid.csv`, `func start`, publish synthetic `dataset.available` event, assert Bronze blob exists at `vendor-abc-20260315-001/2026-03-15/data.parquet`, assert `dataset.bronze.available` message arrives on Service Bus topic

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — start immediately; creates new files only
- **Foundational (Phase 2)**: Depends on Phase 1 completion — BLOCKS all user stories
- **User Story 1 (Phase 3)**: Depends on Phase 2 — is the MVP increment; blocks nothing
- **User Story 2 (Phase 4)**: Depends on Phase 2 (needs `ColumnMapping.MinValue/MaxValue` from T006); can run in parallel with Phase 3
- **User Story 3 (Phase 5)**: Depends on Phase 2; can run in parallel with Phases 3–4 for span/metrics tasks
- **CI/CD (Phase 6)**: Depends on Phases 3–5 (tests must exist before coverage gate is meaningful)
- **Polish (Phase 7)**: Depends on all phases complete

### User Story Dependencies

- **US1 (P1)**: Can start after Phase 2 — no dependency on US2 or US3
- **US2 (P2)**: Can start after Phase 2 — `ColumnMapping.MinValue/MaxValue` (T006) must be committed first; no dependency on US1
- **US3 (P3)**: Can start after Phase 2 — span/metrics tasks (T028, T029) are independent of US1/US2 implementation; T030 (CorrelationId) needs ProcessDatasetFunction.cs changes from T009 (Phase 2)

### Within Each User Story

- Models/interfaces before services (`IBronzeWriter` before `OneLakeBronzeWriter`)
- Services before handler wiring (`AdlsDatasetReader` before `ProcessDatasetCommandHandler`)
- Implementation before tests (spec does not require TDD; tests written alongside implementation)
- Tests verify the story before moving to the next phase

### Parallel Opportunities

- T002, T003 can run in parallel with T001 (different files)
- T004, T005, T006, T010 can all run in parallel (different .csproj / .json files)
- T011, T013, T015 can run in parallel (different files)
- T021, T022, T023 can run in parallel (different files)
- T028, T029 can run in parallel (different files)
- T031, T032 can run in parallel (different test files)
- T034, T035 can run in parallel (different sections of the workflow YAML)
- T036, T037 can run in parallel

---

## Parallel Example: User Story 1

```bash
# After Phase 2 is complete, these US1 tasks can start in parallel:
T011: Update IBronzeWriter.cs WriteAsync signature
T013: Register Polly pipeline in Program.cs; set MaxRetries=0
T015: Register RecordEnricher in DI in Program.cs

# T012 depends on T011 (uses updated IBronzeWriter signature):
T012: Rewrite OneLakeBronzeWriter.cs dynamic Parquet schema

# T014 depends on T013 (Polly pipeline must be registered first):
T014: Update AdlsDatasetReader.cs with Polly retry

# T016 depends on T011, T015 (uses updated WriteAsync, injected RecordEnricher):
T016: Update ProcessDatasetCommandHandler.cs

# Tests can run in parallel once their implementation task is done:
T018 (parallel with T019): Unit tests for OneLakeBronzeWriter
T019 (parallel with T018): Unit tests for ProcessDatasetCommandHandler
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (docker-compose, .env, Config.json)
2. Complete Phase 2: Foundational (constitution/spec violation fixes — CRITICAL)
3. Complete Phase 3: User Story 1 (dynamic Parquet schema, Polly retry, metrics wiring)
4. **STOP and VALIDATE**: Run T020 integration test; verify Bronze Parquet contains all canonical columns
5. Deploy / demo if ready

### Incremental Delivery

1. Setup + Foundational → project builds, binds to emulator
2. User Story 1 → full happy path works end-to-end (MVP!)
3. User Story 2 → all failure paths dead-letter correctly
4. User Story 3 → full observability in Application Insights
5. Each story adds value without breaking previous stories

### Parallel Team Strategy

With multiple developers after Phase 2 is complete:

- **Developer A**: User Story 1 (T011–T020)
- **Developer B**: User Story 2 (T021–T027)
- **Developer C**: User Story 3 (T028–T032) + CI/CD (T033–T035)

---

## Notes

- `[P]` tasks operate on different files with no pending-task dependencies — safe to run in parallel
- `[Story]` label maps each task to a specific user story for traceability
- T006 (`ColumnMapping.MinValue/MaxValue`) is in Phase 2 (Foundational) because US2's range validator depends on it, but it is a pure model-layer change with zero behaviour at Phase 2 completion — it will not break US1
- T008 (delete Infrastructure `DatasetActivitySource.cs`) must be committed before T028 (add spans) to avoid duplicate source registration
- The Polly research finding (research.md §3): **Azure SDK built-in retry must be disabled** (`MaxRetries = 0`) — T013 covers this in the same task as pipeline registration to ensure they cannot be split
- All dead-letter payloads must conform to `contracts/dead-letter-reason.json` schema — validate with a JSON Schema assertion in T027 integration test
