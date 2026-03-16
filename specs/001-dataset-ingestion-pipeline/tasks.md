# Tasks: Dataset Ingestion Pipeline

**Input**: Design documents from `/specs/001-dataset-ingestion-pipeline/`
**Prerequisites**: plan.md ✅ spec.md ✅ research.md ✅ data-model.md ✅ contracts/ ✅ quickstart.md ✅

---

## Phase 1: Setup

**Purpose**: Solution scaffold, project creation, NuGet wiring.

- [X] T001 Create `DatasetProcessingFunction.sln` at repository root with `dotnet new sln`
- [X] T002 Create `src/DatasetProcessingFunction/` isolated-worker Function App project targeting .NET 10 (`dotnet new func --worker-runtime dotnet-isolated`)
- [X] T003 [P] Create `src/DatasetProcessingFunction.Application/` class library project (`dotnet new classlib`)
- [X] T004 [P] Create `src/DatasetProcessingFunction.Domain/` class library project (`dotnet new classlib`)
- [X] T005 [P] Create `src/DatasetProcessingFunction.Infrastructure/` class library project (`dotnet new classlib`)
- [X] T006 [P] Create `tests/DatasetProcessingFunction.UnitTests/` xUnit project (`dotnet new xunit`)
- [X] T007 [P] Create `tests/DatasetProcessingFunction.IntegrationTests/` xUnit project (`dotnet new xunit`)
- [X] T008 Add all projects to `DatasetProcessingFunction.sln`
- [X] T009 Add project references: Host → Application + Infrastructure; Application → Domain; Infrastructure → Domain; UnitTests → Domain + Application; IntegrationTests → Host + Infrastructure
- [X] T010 [P] Add NuGet packages to `src/DatasetProcessingFunction/`: `Microsoft.Azure.Functions.Worker` ≥2.0.0, `Microsoft.Azure.Functions.Worker.Sdk` ≥2.0.5, `Microsoft.Azure.Functions.Worker.Extensions.ServiceBus` ≥5.16.0, `Microsoft.Azure.Functions.Worker.OpenTelemetry` ≥1.1.0, `OpenTelemetry.Extensions.Hosting` ≥1.10.0, `Azure.Monitor.OpenTelemetry.Exporter` ≥1.3.0, `Azure.Identity` ≥1.13.0
- [X] T011 [P] Add NuGet packages to `src/DatasetProcessingFunction.Application/`: `MediatR` ≥12.2.0
- [X] T012 [P] Add NuGet packages to `src/DatasetProcessingFunction.Infrastructure/`: `Azure.Messaging.ServiceBus` ≥7.18.0, `Azure.Storage.Files.DataLake` ≥12.18.0, `Parquet.Net` ≥4.23.0, `CsvHelper` ≥33.0.0, `Azure.Identity` ≥1.13.0, `Microsoft.Extensions.Configuration.AzureAppConfiguration` ≥8.0.0
- [X] T013 [P] Add NuGet packages to `tests/DatasetProcessingFunction.UnitTests/` and `tests/DatasetProcessingFunction.IntegrationTests/`: `Moq` ≥4.20.0, `FluentAssertions` ≥6.12.0, `coverlet.collector` ≥6.0.0
- [X] T014 Copy `src/DatasetProcessingFunction/local.settings.json.template` with emulator defaults per `quickstart.md`; add `local.settings.json` to `.gitignore`

**Checkpoint**: `dotnet build DatasetProcessingFunction.sln` passes with zero errors.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Cross-cutting infrastructure that every user story depends on — domain primitives,
DI wiring, OTel, host.json, Bicep modules, and CI/CD pipeline skeleton.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

### Domain Primitives

- [X] T015 [P] Create `src/DatasetProcessingFunction.Domain/ValueObjects/DatasetId.cs` — immutable record with `Value: string`; equality by value; throw `ArgumentException` on null/empty
- [X] T016 [P] Create `src/DatasetProcessingFunction.Domain/Enums/ProcessingStatus.cs` — enum: `Received`, `Parsing`, `Validating`, `Transforming`, `Writing`, `Completed`, `Failed`
- [X] T017 [P] Create `src/DatasetProcessingFunction.Domain/ValueObjects/ValidationFailure.cs` — immutable record: `RowIndex`, `FieldName`, `Rule`, `Detail`
- [X] T018 Create `src/DatasetProcessingFunction.Domain/ValueObjects/ValidationResult.cs` — immutable; `IsSuccess`, `PassCount`, `FailCount`, `Failures: IReadOnlyList<ValidationFailure>`; factory `Success(int)` + `Failure(IReadOnlyList<ValidationFailure>)` (depends on T017)
- [X] T019 [P] Create `src/DatasetProcessingFunction.Domain/ValueObjects/RawRecord.cs` — immutable record: `RowIndex: int`, `Fields: IReadOnlyDictionary<string,string>`
- [X] T020 Create `src/DatasetProcessingFunction.Domain/ValueObjects/CanonicalRecord.cs` — immutable record: `SiteId`, `Timestamp: DateTimeOffset`, `IngestionTime: DateTimeOffset`, `SourceDatasetId`, `SchemaVersion`, `Fields: IReadOnlyDictionary<string,object>` (depends on T015)
- [X] T021 Create `src/DatasetProcessingFunction.Domain/ValueObjects/ProcessingMetrics.cs` — immutable record: `DatasetId`, `RecordsProcessed`, `ValidationPassCount`, `ValidationFailCount`, `ProcessingDurationMs: long`, `BronzePath: Uri?`

### Application Interfaces

- [X] T022 [P] Create `src/DatasetProcessingFunction.Application/Interfaces/IDatasetReader.cs` — `Task<Stream> ReadAsync(Uri storagePath, CancellationToken ct)`
- [X] T023 [P] Create `src/DatasetProcessingFunction.Application/Interfaces/IBronzeWriter.cs` — `Task<Uri> WriteAsync(DatasetId id, DateOnly date, IReadOnlyList<CanonicalRecord> records, CancellationToken ct)`
- [X] T024 [P] Create `src/DatasetProcessingFunction.Application/Interfaces/ISchemaRegistry.cs` — `Task<VendorSchemaMapping?> GetAsync(string vendorId, string schemaVersion, CancellationToken ct)`
- [X] T025 [P] Create `src/DatasetProcessingFunction.Application/Interfaces/IEventPublisher.cs` — `Task PublishAsync(string eventType, object payload, string correlationId, CancellationToken ct)`

### Infrastructure Entities

- [X] T026 Create `src/DatasetProcessingFunction.Infrastructure/SchemaRegistry/VendorSchemaMapping.cs` — POCO with `VendorId`, `SchemaVersion`, `Delimiter: char`, `Encoding: string`, `ColumnMappings: List<ColumnMapping>`, `RequiredFields: List<string>`; nested `ColumnMapping` record: `VendorColumn`, `CanonicalField`, `DataType`, `UnitConversion?`

### OpenTelemetry + Host Wiring

- [X] T027 Create `src/DatasetProcessingFunction.Infrastructure/Telemetry/DatasetActivitySource.cs` — `internal static readonly ActivitySource Source = new("DatasetProcessingFunction", "1.0.0")`
- [X] T028 Write `src/DatasetProcessingFunction/host.json` — set `"version": "2.0"`, `"telemetryMode": "OpenTelemetry"`, Service Bus extension with `autoComplete: false`, `maxConcurrentCalls: 16`, `prefetchCount: 16`, `maxAutoLockRenewalDuration: "00:05:00"` per constitution Principle VIII
- [X] T029 Write `src/DatasetProcessingFunction/Program.cs` — `FunctionsApplication.CreateBuilder(args)`; register `AddOpenTelemetry().UseFunctionsWorkerDefaults().UseAzureMonitorExporter()`; `AddOpenTelemetry(b => b.IncludeScopes = true)`; `AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<ProcessDatasetCommand>())`; Singleton registrations for `ServiceBusClient`, `ServiceBusSender`, `DataLakeServiceClient`; interface-to-implementation bindings for `IDatasetReader`, `IBronzeWriter`, `ISchemaRegistry`, `IEventPublisher`

### Bicep Infrastructure Skeleton

- [X] T030 [P] Create `infrastructure/main.bicep` — wire all modules; accept `appName`, `location`, `serviceBusNamespace`, `onelakeEndpoint`, `otelServiceName` as parameters
- [X] T031 [P] Create `infrastructure/main.parameters.json` — externalise all environment-specific values; include `OTEL_SERVICE_NAME` parameter
- [X] T032 [P] Create `infrastructure/modules/function-app.bicep` — Premium plan (EP1), System-Assigned Managed Identity, `WEBSITE_RUN_FROM_PACKAGE=1`, staging deployment slot, warmup trigger app setting
- [X] T033 [P] Create `infrastructure/modules/service-bus.bicep` — Standard/Premium namespace; `dataset-events` topic; `dataset-ingestion` subscription with DLQ; `maxDeliveryCount: 5`; role assignment `Azure Service Bus Data Receiver` to Function App identity
- [X] T034 [P] Create `infrastructure/modules/storage.bicep` — dedicated storage account for Function App internal use (`AzureWebJobsStorage`); separate from Bronze storage
- [X] T035 [P] Create `infrastructure/modules/app-insights.bicep` — Application Insights workspace; `APPLICATIONINSIGHTS_CONNECTION_STRING` output
- [X] T036 [P] Create `infrastructure/modules/key-vault.bicep` — Key Vault; `get`+`list` secret permissions for Function App Managed Identity via `Microsoft.Authorization/roleAssignments`
- [X] T037 Create `azure.yaml` — AZD manifest pointing to `infrastructure/main.bicep` as the single `azd up` entry point

### CI/CD Pipeline Skeleton

- [X] T038 Create `.github/workflows/ci-cd.yml` (or `.azure-pipelines/ci-cd.yml`) — define stages: Build → Unit Tests (coverage ≥80% gate) → Integration Tests → Bicep What-If (infra PRs) → Deploy to Staging Slot (`AzureFunctionApp@2`, `deployToSlotOrASE: true`) → Health Check → Slot Swap (`AzureAppServiceManage@0`) → OTel Validation (non-blocking); configure Workload Identity Federation (OIDC) service connection; use `AzureFunctionApp@2` task

**Checkpoint**: Foundation complete. `dotnet build` clean; `host.json` OTel mode set; Bicep and CI/CD skeletons in place. User story implementation can now begin.

---

## Phase 3: User Story 1 — Successful End-to-End Dataset Processing (Priority: P1) 🎯 MVP

**Goal**: A `dataset.available` event triggers the full pipeline — retrieve CSV from ADLS,
parse, validate, transform, enrich, write Parquet to Bronze, publish `dataset.bronze.available`.

**Independent Test**: Publish a synthetic `dataset.available` event in the integration test
pointing to `vendor-abc-v2-valid.csv` in Azurite; assert records appear in the Bronze output
path and a `dataset.bronze.available` message is on the emulator topic.

### Tests for User Story 1 ⚠️ Write these FIRST — confirm they FAIL before implementing

- [X] T039 [P] [US1] Write unit test `tests/DatasetProcessingFunction.UnitTests/Domain/CsvParserServiceTests.cs` — tests: valid CSV with comma delimiter is parsed to `RawRecord` list; header row detected; row count correct; UTF-8 check passes; non-UTF-8 stream returns error
- [X] T040 [P] [US1] Write unit test `tests/DatasetProcessingFunction.UnitTests/Domain/SchemaTransformerTests.cs` — tests: vendor columns mapped to canonical fields; datetime normalised to ISO 8601 UTC; unit conversion applied (kW→W); enrichment fields (`site_id`, `ingestion_time`) present in `CanonicalRecord`
- [X] T041 [P] [US1] Write unit test `tests/DatasetProcessingFunction.UnitTests/Application/ProcessDatasetCommandHandlerTests.cs` — tests: successful command dispatches to all domain services in correct order; `IBronzeWriter.WriteAsync` called once; `IEventPublisher.PublishAsync` called once with `dataset.bronze.available`; `ProcessDatasetResult.Success == true`
- [X] T042 [P] [US1] Write integration test `tests/DatasetProcessingFunction.IntegrationTests/ProcessDatasetEndToEndTests.cs` — end-to-end test seeding `vendor-abc-v2-valid.csv` into Azurite, publishing `dataset.available` event to SB emulator, asserting Bronze Parquet file written and `dataset.bronze.available` message received

### Implementation for User Story 1

- [X] T043 [US1] Implement `src/DatasetProcessingFunction.Domain/Services/CsvParserService.cs` — `IAsyncEnumerable<RawRecord> ParseAsync(Stream csv, char delimiter, CancellationToken ct)`; verify UTF-8 BOM/encoding before parsing (FR-006b); use `CsvHelper` with `CsvConfiguration { Delimiter = delimiter.ToString() }`; normalise mixed line endings; yield `RawRecord` per data row (depends on T019, T039)
- [X] T044 [US1] Implement `src/DatasetProcessingFunction.Domain/Services/SchemaTransformer.cs` — `IReadOnlyList<CanonicalRecord> Transform(IReadOnlyList<RawRecord> records, VendorSchemaMapping mapping)`: map vendor columns to canonical fields; cast to typed values per `FieldDataType`; apply `UnitConversion` where present; normalise all datetime fields to ISO 8601 UTC (FR-014–015); (depends on T019, T020, T026, T040)
- [X] T045 [US1] Implement `src/DatasetProcessingFunction.Domain/Services/RecordEnricher.cs` — `CanonicalRecord Enrich(CanonicalRecord record, string datasetId, DateTimeOffset ingestionTime)`: attach `site_id` (derived from `datasetId`), `ingestion_time`, `source_dataset_id`, `schema_version` (FR-016)
- [X] T046 [US1] Implement `src/DatasetProcessingFunction.Infrastructure/Storage/AdlsDatasetReader.cs` — `IDatasetReader`; `DataLakeServiceClient.GetFileSystemClient(...).GetFileClient(path).OpenReadAsync()`; `DefaultAzureCredential`; propagate `CancellationToken` (depends on T022)
- [X] T047 [US1] Implement `src/DatasetProcessingFunction.Infrastructure/Storage/OneLakeBronzeWriter.cs` — `IBronzeWriter`; build `ParquetSchema` from `CanonicalRecord` properties; serialize records to `MemoryStream` using `Parquet.Net`; `DataLakeFileClient.DeleteIfExistsAsync()` on partition path (idempotent overwrite per FR-018); `DataLakeFileClient.UploadAsync(stream)`; return Bronze ADLS URI (depends on T020, T023)
- [X] T048 [US1] Implement `src/DatasetProcessingFunction.Infrastructure/SchemaRegistry/BlobSchemaRegistry.cs` — `ISchemaRegistry`; load JSON blob `schema-registry/{vendorId}-{schemaVersion}.json`; deserialise to `VendorSchemaMapping`; cache in `ConcurrentDictionary<string, VendorSchemaMapping>` for instance lifetime (depends on T024, T026)
- [X] T049 [US1] Implement `src/DatasetProcessingFunction.Infrastructure/Messaging/ServiceBusEventPublisher.cs` — `IEventPublisher`; `ServiceBusSender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(payload)) { ContentType = "application/json", CorrelationId = correlationId, Subject = eventType })` (depends on T025)
- [X] T050 [US1] Implement `src/DatasetProcessingFunction.Application/Commands/ProcessDatasetCommand.cs` and `ProcessDatasetResult.cs` — MediatR `IRequest<ProcessDatasetResult>`; fields: `DatasetId`, `StoragePath`, `VendorId`, `SchemaVersion`, `CorrelationId`
- [X] T051 [US1] Implement `src/DatasetProcessingFunction.Application/Commands/ProcessDatasetCommandHandler.cs` — orchestrate: (1) `ISchemaRegistry.GetAsync` → reject unknown schema; (2) `IDatasetReader.ReadAsync`; (3) `CsvParserService.ParseAsync` using mapping delimiter; (4) `DataQualityValidator.Validate` (stub for US1 — returns all pass); (5) `SchemaTransformer.Transform`; (6) `RecordEnricher.Enrich` per record; (7) `IBronzeWriter.WriteAsync`; (8) `IMediator.Publish(DatasetBronzeAvailableNotification)`; wrap each step in OTel Activity span via `DatasetActivitySource.Source`; record `ProcessingMetrics` (depends on T022–T025, T043–T049, T050)
- [X] T052 [US1] Implement `src/DatasetProcessingFunction.Application/Notifications/DatasetBronzeAvailableNotification.cs` — MediatR `INotification`; fields per `data-model.md`
- [X] T053 [US1] Implement `src/DatasetProcessingFunction.Application/Notifications/DatasetBronzeAvailablePublisher.cs` — `INotificationHandler<DatasetBronzeAvailableNotification>`; calls `IEventPublisher.PublishAsync("dataset.bronze.available", payload, correlationId, ct)` (depends on T025, T052)
- [X] T054 [US1] Implement `src/DatasetProcessingFunction/Functions/ProcessDatasetFunction.cs` — thin trigger: `[Function("ProcessDataset")]`, `[ServiceBusTrigger("dataset-events", "dataset-ingestion", ...)]`; deserialise message body to `DatasetAvailableEvent`; call `IMediator.Send(new ProcessDatasetCommand(...))`; on success call `messageActions.CompleteMessageAsync`; on failure call `messageActions.DeadLetterMessageAsync` with structured reason (depends on T050, T051)
- [X] T055 [US1] Add `tests/DatasetProcessingFunction.IntegrationTests/fixtures/vendor-abc-v2-valid.csv` and `tests/DatasetProcessingFunction.IntegrationTests/fixtures/schema-registry/vendor-abc-v2.json` fixture files

**Checkpoint**: User Story 1 fully functional. Run T042 integration test end-to-end. Bronze Parquet file present; `dataset.bronze.available` published.

---

## Phase 4: User Story 2 — Data Quality Failure Handling (Priority: P2)

**Goal**: CSV files with missing `timestamp`, out-of-range numerics, or unknown schema are
detected, logged with structured reason, and dead-lettered — zero records reach Bronze.

**Independent Test**: Publish events referencing `vendor-abc-v2-missing-timestamp.csv` and
`vendor-abc-v2-invalid-range.csv` fixtures; assert no Bronze file written; assert message on
DLQ with structured reason payload in message body.

### Tests for User Story 2 ⚠️ Write FIRST — confirm FAIL before implementing

- [X] T056 [P] [US2] Write unit test `tests/DatasetProcessingFunction.UnitTests/Domain/DataQualityValidatorTests.cs` — tests: record missing `timestamp` → `ValidationFailure` with `Rule: "RequiredField"`; record with out-of-range numeric → `ValidationFailure` with `Rule: "NumericRange"`; all-pass dataset → `ValidationResult.IsSuccess == true`; unknown schema header → schema validation fails entire dataset
- [X] T057 [P] [US2] Write integration test fixture files `tests/DatasetProcessingFunction.IntegrationTests/fixtures/vendor-abc-v2-missing-timestamp.csv` and `vendor-abc-v2-invalid-range.csv`
- [X] T058 [P] [US2] Extend `tests/DatasetProcessingFunction.IntegrationTests/ProcessDatasetEndToEndTests.cs` — add tests: missing-timestamp event → no Bronze file + message on DLQ; invalid-range event → no Bronze file + DLQ message with structured reason

### Implementation for User Story 2

- [X] T059 [US2] Implement `src/DatasetProcessingFunction.Domain/Services/DataQualityValidator.cs` — `ValidationResult Validate(IReadOnlyList<RawRecord> records, VendorSchemaMapping mapping)`: (1) for each record check all fields in `mapping.RequiredFields` are non-null/empty → `ValidationFailure("RequiredField")`; (2) for each numeric field parse and check configured range → `ValidationFailure("NumericRange")`; (3) verify header column set matches mapping column names → if mismatch return immediate failure with `Rule: "UnknownSchema"` (FR-009–011); return `ValidationResult.Failure(failures)` if any failures (depends on T017, T018, T019, T026, T056)
- [X] T060 [US2] Update `src/DatasetProcessingFunction.Application/Commands/ProcessDatasetCommandHandler.cs` — replace stub validation with full `DataQualityValidator.Validate` call; if `ValidationResult.IsSuccess == false`: emit structured OTel log entry with `dataset_id`, `error_type: "ValidationFailed"`, failure summary; emit `ProcessingMetrics` with `ValidationFailCount > 0`; throw domain exception to trigger Service Bus nack → dead-letter (FR-019: do NOT call `IBronzeWriter.WriteAsync`) (depends on T051, T059)
- [X] T061 [US2] Update `src/DatasetProcessingFunction/Functions/ProcessDatasetFunction.cs` — catch domain validation exception; call `messageActions.DeadLetterMessageAsync(message, "ValidationFailed", JsonSerializer.Serialize(validationSummary), ct)` with structured reason in dead-letter reason + description (depends on T054, T060)
- [X] T062 [US2] Implement `src/DatasetProcessingFunction.Domain/Services/CsvParserService.cs` encoding check — before creating `CsvReader`, detect encoding via `StreamReader`; if not UTF-8 throw `UnsupportedEncodingException` with detected encoding name (FR-006b); update `CsvParserServiceTests.cs` (T039) to cover this path (depends on T043)

**Checkpoint**: User Stories 1 and 2 independently functional. Invalid datasets dead-lettered; valid datasets write to Bronze. Run T058 integration tests.

---

## Phase 5: User Story 3 — Observability and Metrics Emission (Priority: P3)

**Goal**: Every execution (success or failure) emits correlated OTel traces to Application
Insights with `OTEL_SERVICE_NAME` tag, custom metrics, and per-domain-operation child spans.

**Independent Test**: Run a batch of 5 synthetic events (mix of valid and invalid) against a
real Application Insights instance; query for custom metric `records_processed` and trace
`dataset.csv.parse` — all must appear with matching `CorrelationId` and `OTEL_SERVICE_NAME`.

### Tests for User Story 3 ⚠️ Write FIRST — confirm FAIL before implementing

- [X] T063 [P] [US3] Write unit test `tests/DatasetProcessingFunction.UnitTests/Application/ProcessDatasetCommandHandlerTests.cs` — add assertions: `DatasetActivitySource.Source` starts named spans `dataset.csv.parse`, `dataset.validation.run`, `dataset.transform`, `dataset.bronze.write`; verify span attributes include `dataset_id`
- [X] T064 [P] [US3] Write unit test for `ProcessingMetrics` emission — after successful handler execution, `ProcessingMetrics.RecordsProcessed`, `ValidationPassCount`, `ProcessingDurationMs` are all non-zero; after failed validation, `ValidationFailCount > 0` and `BronzePath == null`

### Implementation for User Story 3

- [X] T065 [US3] Wrap each domain operation in `ProcessDatasetCommandHandler.cs` with a named OTel `Activity` span via `DatasetActivitySource.Source.StartActivity(...)` — span names: `dataset.csv.parse`, `dataset.validation.run`, `dataset.transform`, `dataset.bronze.write`; add `dataset_id` tag to each span (FR-023); ensure `CorrelationId` from inbound event is set as `Activity.TraceId` context via `ActivityContext.Parse(correlationId)` at function trigger entry (depends on T051, T027)
- [X] T066 [US3] Create `src/DatasetProcessingFunction.Infrastructure/Telemetry/ProcessingMetricsEmitter.cs` — use `System.Diagnostics.Metrics.Meter("DatasetProcessingFunction")`; define counters: `records_processed`, `validation_pass_count`, `validation_fail_count`; histogram: `processing_duration_ms`; emit at end of `ProcessDatasetCommandHandler` with `dataset_id` tag (FR-021); register `Meter` as OTel instrument in `Program.cs` (depends on T021, T029)
- [X] T067 [US3] Verify `src/DatasetProcessingFunction/Program.cs` has `builder.Logging.AddOpenTelemetry(b => b.IncludeScopes = true)` and that the default Application Insights log-filter rule is NOT present (constitution Principle VIII + IX) (depends on T029)
- [X] T068 [US3] Add `OTEL_SERVICE_NAME` and `APPLICATIONINSIGHTS_CONNECTION_STRING` output parameters to `infrastructure/modules/function-app.bicep` app settings block; add `OTEL_SERVICE_NAME` to `infrastructure/main.parameters.json` (depends on T032)

**Checkpoint**: All three user stories independently functional. OTel spans visible; custom metrics recorded; correlation IDs consistent across host and worker process.

---

## Phase 6: Polish & Cross-Cutting Concerns

- [X] T069 [P] Add `tests/DatasetProcessingFunction.IntegrationTests/fixtures/schema-registry/vendor-abc-v2.json` schema mapping fixture with delimiter, column mappings, required fields, and unit conversions matching test CSVs
- [X] T070 [P] Add XML doc comments to all public interfaces in `src/DatasetProcessingFunction.Application/Interfaces/` — document method contract, exception types, and cancellation behaviour
- [X] T071 [P] Add `README.md` at repository root referencing `specs/001-dataset-ingestion-pipeline/quickstart.md` for local dev instructions
- [X] T072 [P] Add `.editorconfig` at repository root — enforce `dotnet_style_qualification_for_field = false`, `dotnet_diagnostic.IDE0058.severity = none`; add `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` to all `.csproj` files
- [X] T073 Add `Dependabot` configuration (`.github/dependabot.yml`) for NuGet package updates on a weekly schedule (constitution Principle VI)
- [X] T074 Run `dotnet test DatasetProcessingFunction.sln --collect:"XPlat Code Coverage"` and confirm Domain + Application coverage ≥ 80%; fix any gaps (SC-007)
- [ ] T075 Run `az deployment group what-if` against dev resource group using `infrastructure/main.bicep`; attach output as PR artefact template comment

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: No dependencies — start immediately
- **Phase 2 (Foundational)**: Depends on Phase 1 completion — **blocks all user stories**
- **Phase 3 (US1 — P1)**: Depends on Phase 2 — no other user story dependency
- **Phase 4 (US2 — P2)**: Depends on Phase 2; integrates with US1 handler (T051, T054)
- **Phase 5 (US3 — P3)**: Depends on Phase 2; adds instrumentation on top of US1 + US2
- **Phase 6 (Polish)**: Depends on all user story phases complete

### User Story Dependencies

- **US1 (P1)**: Independent after Foundational — core pipeline path
- **US2 (P2)**: Builds on US1 handler (T051) and function trigger (T054) — sequential after US1
- **US3 (P3)**: Adds instrumentation to US1 + US2 — sequential after US2

### Within Each User Story

- Tests MUST be written and FAIL before implementation begins
- Domain services (T043–T045) → Application handler (T051) → Function trigger (T054)
- Infrastructure adapters can be implemented in parallel with domain services

### Parallel Opportunities

- T003–T007: All project creations in parallel
- T010–T013: All NuGet package additions in parallel
- T015–T021: All domain primitive classes in parallel
- T022–T025: All application interfaces in parallel
- T030–T037: All Bicep modules in parallel
- T039–T042 (US1 tests) in parallel
- T043–T049 (US1 implementations): ADLS reader, Bronze writer, schema registry, SB publisher all in parallel once T022–T025 done
- T056–T058 (US2 tests) in parallel
- T063–T064 (US3 tests) in parallel

---

## Implementation Strategy

### MVP (User Story 1 only)

1. Complete Phase 1 (Setup)
2. Complete Phase 2 (Foundational) — critical blocker
3. Complete Phase 3 (US1) — test first (T039–T042), then implement (T043–T055)
4. **STOP and VALIDATE**: run T042 end-to-end integration test
5. Deploy to staging slot via CI/CD; verify Bronze Parquet file and `dataset.bronze.available` event

### Incremental Delivery

1. Setup + Foundational → foundation ready
2. US1 → tested → deployed (MVP: valid dataset → Bronze → downstream event)
3. US2 → tested → deployed (adds: invalid dataset → DLQ, zero Bronze pollution)
4. US3 → tested → deployed (adds: OTel traces + metrics in App Insights)
5. Polish → code coverage gate met; Dependabot enabled

---

## Notes

- `[P]` = parallelizable (no shared file dependencies)
- `[USN]` = maps task to User Story N for traceability
- All tests MUST be written and confirmed failing before implementation (constitution Principle VII)
- Commit after each checkpoint using Conventional Commits (`feat:`, `test:`, `infra:`)
- Direct production deploys without staging slot swap are prohibited (constitution Workflow §8)
