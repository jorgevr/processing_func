# Feature Specification: Dataset Ingestion Pipeline (Raw → Bronze)

**Feature Branch**: `002-dataset-ingestion-pipeline`
**Created**: 2026-04-15
**Updated**: 2026-04-15
**Status**: Draft
**Scope**: This function covers the **raw → Bronze** step of the medallion pipeline only.
Silver-layer processing (Bronze → Silver, gap detection, quality metrics, ML features) is
handled by a separate function triggered by `dataset.bronze.available`.

---

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Successful End-to-End Dataset Processing (Priority: P1)

A vendor dataset arrives in storage and a `dataset.available` event is published on the
message broker. The pipeline automatically picks up the event, retrieves the file, parses and
validates the CSV, transforms the records to the canonical data model, standardises units and
datetimes, enriches them with platform metadata, and persists the structured records to the
Bronze layer — all without operator intervention.

**Why this priority**: This is the core pipeline flow. Without it no downstream analytics
can be fed, making it the single most valuable piece of end-to-end behaviour.

**Independent Test**: Can be fully tested by publishing a synthetic `dataset.available` event
referencing a well-formed CSV fixture in a local storage emulator and asserting that matching
records appear in the Bronze output store with correct field mappings, unit conversions, and
enrichment metadata.

**Acceptance Scenarios**:

1. **Given** a valid `dataset.available` event referencing a storage path containing a
   well-formed CSV with recognised vendor headers, **When** the function processes the event,
   **Then** all CSV rows are parsed, validated, transformed to the canonical model (including
   numeric unit conversion where configured), enriched with `site_id` and `ingestion_time`,
   and written to the Bronze layer under the correct partition keyed by the dataset identifier
   — **and** the written output contains all mapped canonical fields (not only enrichment
   metadata fields).

2. **Given** the same `dataset.available` event is delivered twice (duplicate delivery),
   **When** the function processes the second delivery, **Then** no duplicate records are
   written to the Bronze layer — the output is identical to that of the first delivery
   (atomic replace semantics).

3. **Given** a valid event, **When** processing completes successfully, **Then** structured
   telemetry is emitted containing dataset identifier, record count, processing duration,
   validation pass/fail counts, and zero error entries in the dead-letter queue — **and** a
   `dataset.bronze.available` event is published on the message broker carrying `dataset_id`,
   `record_count`, `bronze_path`, and the propagated `correlation_id`.

---

### User Story 2 - Data Quality Failure Handling (Priority: P2)

A vendor CSV file fails one or more data-quality checks (missing required `timestamp` field,
out-of-range numeric values, unrecognised schema, empty file, or unsupported encoding). The
pipeline detects, records, and routes the failure without corrupting the Bronze layer or
silently discarding the message.

**Why this priority**: Silent data corruption downstream is worse than a visible failure.
Reliable rejection with full observability is a prerequisite for production trust.

**Independent Test**: Can be fully tested by publishing an event referencing a CSV fixture
with deliberately invalid records and asserting that the message lands on the dead-letter
queue, no partial records reach the Bronze layer, and a structured validation-failure payload
is attached to the dead-lettered message.

**Acceptance Scenarios**:

1. **Given** a CSV file where one or more records are missing the required `timestamp` field,
   **When** the function runs data-quality validation, **Then** the affected records are
   rejected, a structured error log entry identifying the failing rows and rule is emitted,
   and the message is dead-lettered with `error_type: "ValidationFailed"`.

2. **Given** a CSV file where numeric fields contain values outside the `min_value`/`max_value`
   boundaries declared in the vendor schema mapping for that field, **When** validation runs,
   **Then** those records are flagged, the dataset fails quality gates, and the event is routed
   to the dead-letter queue with a structured reason payload including fail count and first 10
   failure details.

3. **Given** a CSV file whose header row does not match any known vendor-schema mapping,
   **When** the function attempts schema normalisation, **Then** processing halts, an
   unknown-schema error is emitted as structured telemetry, and the message is dead-lettered
   with `error_type: "UnknownSchema"` carrying `vendor_id` and `schema_version`.

4. **Given** a CSV file containing zero data rows (header only), **When** the function
   processes the event, **Then** the message is dead-lettered with `error_type: "EmptyDataset"`;
   no Bronze write is performed and no downstream event is emitted.

5. **Given** a CSV file with a non-UTF-8 encoding, **When** the function attempts to parse
   the file, **Then** the message is dead-lettered with `error_type: "UnsupportedEncoding"`
   and the detected encoding is included in the reason payload.

---

### User Story 3 - Observability and Metrics Emission (Priority: P3)

Platform operators and data engineers can observe the health of the ingestion pipeline in
near-real time through structured logs and metrics — without needing access to raw function
logs.

**Why this priority**: Observability is a production-readiness requirement; without it
silent failures cannot be detected.

**Independent Test**: Can be fully tested by processing a batch of synthetic events and
querying the telemetry backend for the expected custom metrics (`records_processed`,
`validation_failures`, `processing_duration_ms`) and correlated log entries sharing a
`CorrelationId`.

**Acceptance Scenarios**:

1. **Given** a successfully processed dataset, **When** the function emits telemetry,
   **Then** the telemetry backend records a custom metric entry containing `dataset_id`,
   `record_count`, `validation_pass_count`, `validation_fail_count`, and
   `processing_duration_ms` correlated by a single `CorrelationId`.

2. **Given** a failed dataset processing attempt, **When** the error is logged, **Then** the
   structured log entry contains `dataset_id`, `error_type`, `error_detail`, and the same
   `CorrelationId` as the triggering event, enabling end-to-end trace reconstruction.

---

### Edge Cases

- What happens when the schema registry is temporarily unavailable when loading the
  vendor schema mapping? → Treated as a transient failure; same 3-attempt exponential
  retry as the CSV file read (FR-013a). A missing mapping (blob not found) is
  non-retriable and results in an UnknownSchema dead-letter (FR-011).
- What happens when the storage file referenced in the event no longer exists or is
  inaccessible at processing time? → Transient failures are retried (up to 3 attempts,
  exponential back-off); after exhaustion the Service Bus delivery count increments toward
  dead-lettering. Permanent 404 (file genuinely missing) follows the same retry path and
  eventually dead-letters with the underlying exception in the reason payload.
- What happens when a CSV file is empty (header row only, zero data rows)? → The event is
  dead-lettered with reason `EmptyDataset`; a Bronze write with zero records is not performed
  and no `dataset.bronze.available` event is emitted.
- What happens when a partially written Bronze output exists from a previously interrupted
  run for the same dataset identifier? → The partition is atomically replaced on
  re-processing; partial writes are never visible to consumers.
- Non-UTF-8 encoded files are rejected and dead-lettered with the detected encoding in the
  reason payload; transcoding is not performed. Mixed line endings (`\r\n` / `\n`) are
  normalised by the CSV parser and do not cause rejection.
- What happens when the Bronze layer write fails mid-batch — are partial writes visible? →
  No. The output is written to a memory buffer first; the storage upload is a single atomic
  operation. A failed upload leaves the previous partition intact.
- How does the function behave when the `dataset.available` event payload is malformed or
  missing the storage path field? → Dead-lettered immediately on the first delivery attempt
  with a structured reason payload; no retry.
- What happens when a `ColumnMapping` has `UnitConversion` set to an unrecognised key? →
  The record is passed through unchanged; an unrecognised conversion key is treated
  as absent (no conversion applied).
- What happens when a `VendorSchemaMapping` has no `ColumnMapping` entry with
  `canonical_field: "site_id"`? → The schema is treated as unrecognised; processing halts
  with an UnknownSchema error and the event is dead-lettered (FR-011, FR-016).

---

## Requirements *(mandatory)*

### Functional Requirements

#### Event Consumption

- **FR-001**: The system MUST trigger processing exclusively from a `dataset.available`
  integration event delivered via the configured Service Bus **queue**; polling or manual
  triggers are not permitted. The Service Bus tier in use supports queues only — topics and
  subscriptions are not used.
- **FR-001a**: The Service Bus queue name MUST be read from application settings
  (`SERVICEBUS_QUEUE_NAME`, default `raw-energy-events`) and MUST NOT be hard-coded in the
  function trigger attribute. The fully-qualified namespace is supplied via a dedicated
  connection setting for production; local development uses the Service Bus emulator
  connection string with `UseDevelopmentEmulator=true`.
- **FR-002**: The system MUST extract the storage path and dataset identifier from the
  `dataset.available` event payload before initiating any downstream processing.
- **FR-003**: The system MUST dead-letter any event whose payload is malformed or missing
  mandatory fields (`dataset_id`, `storage_path`) after a single delivery attempt with a
  structured reason payload. The `correlation_id` field is optional; if absent the function
  MUST generate a new `Guid`-based `correlation_id` at the trigger boundary, log a warning
  that no upstream correlation was provided, and proceed — the event is NOT dead-lettered
  solely for a missing `correlation_id`.

#### Dataset Retrieval

- **FR-004**: The system MUST retrieve the CSV file from the storage path specified in the
  event using Managed Identity; shared-key connection strings are prohibited in production.
- **FR-005**: The system MUST treat a missing or inaccessible file as a transient failure,
  applying exponential back-off retry with a maximum of 3 attempts, a 2-second base delay,
  and a 30-second maximum delay before propagating the exception and allowing the message
  broker delivery count to increment toward dead-lettering.

#### CSV Parsing

- **FR-006**: The system MUST auto-detect the presence and position of a CSV header row and
  use it to map columns to named fields.
- **FR-006a**: The CSV field delimiter MUST be read from the vendor schema mapping for the
  matching `vendor_id` + `schema_version`; the delimiter is not assumed to be a comma.
  Supported delimiters: comma (`,`), semicolon (`;`), and tab (`\t`).
- **FR-006b**: The system MUST verify that the CSV file is UTF-8 encoded before parsing;
  files with a non-UTF-8 encoding MUST be rejected and dead-lettered with a structured reason
  payload identifying the detected encoding — transcoding is prohibited.
- **FR-006c**: A CSV file containing zero data rows (header only) MUST be rejected and
  dead-lettered with reason `EmptyDataset`; it MUST NOT produce an empty Bronze partition or
  a downstream `dataset.bronze.available` event.
- **FR-007**: The system MUST convert CSV field values to appropriate data types (integer,
  decimal, datetime, string) based on the canonical schema definition; unparseable values
  MUST be recorded as validation failures.
- **FR-008**: The system MUST handle CSV files with at least 10,000 rows without exceeding
  the function's configured timeout.

#### Data Quality Validation

- **FR-009**: The system MUST validate that every record contains a non-null, parseable
  `timestamp` field; records failing this check MUST be rejected.
- **FR-010**: The system MUST validate that all numeric fields whose vendor schema mapping
  includes a non-null `min_value` or `max_value` fall within those boundaries; out-of-range
  records MUST be rejected. Fields without range boundaries defined are not range-checked.
- **FR-011**: The system MUST verify that the CSV header schema matches a known
  vendor-to-canonical mapping; files with an unrecognised schema MUST cause the entire
  dataset to fail (no partial acceptance) and be dead-lettered with `error_type: "UnknownSchema"`.
- **FR-012**: The system MUST emit a structured validation summary (pass count, fail count,
  failure reasons per rule) as telemetry regardless of overall outcome.

#### Schema Normalisation & Enrichment

- **FR-013**: The system MUST map vendor-specific column names to the platform's canonical
  field names using a configurable vendor schema registry; hard-coded column name mappings
  are prohibited.
- **FR-013a**: The system MUST treat an unavailable schema registry as a transient failure,
  applying the same exponential back-off retry policy as FR-005 (3 attempts, 2-second base
  delay, 30-second maximum delay) before propagating the exception and allowing the Service
  Bus delivery count to increment toward dead-lettering. A permanently absent mapping (HTTP
  404 / blob not found) is non-retriable and triggers FR-011 (UnknownSchema).
- **FR-014**: The system MUST standardise all datetime fields to ISO 8601 UTC format during
  transformation.
- **FR-015**: The system MUST standardise numeric unit values (e.g., kW → W, °F → °C) where
  a `unit_conversion` is defined in the column mapping; records without a defined conversion
  are passed through unchanged. Supported conversions: `kw_to_w` (× 1 000), `mw_to_w`
  (× 1 000 000), `kwh_to_wh` (× 1 000), `mwh_to_wh` (× 1 000 000). An unrecognised
  conversion key is treated as absent.
- **FR-016**: The system MUST attach the following enrichment fields to every output record:
  `site_id` (value extracted from the CSV column whose `VendorSchemaMapping` entry has
  `canonical_field: "site_id"`; a vendor schema that has no such mapping is treated as
  unrecognised and triggers FR-011), `ingestion_time` (UTC timestamp of processing),
  `source_dataset_id`, and `schema_version`.
- **FR-016a**: A `VendorSchemaMapping` MUST contain at least one `ColumnMapping` with
  `canonical_field: "timestamp"`. If no such mapping exists, the schema is treated as
  unrecognised and the event is dead-lettered with `error_type: "UnknownSchema"` carrying
  `vendor_id` and `schema_version` — identical treatment to the missing `site_id` mapping
  rule in FR-016 and FR-011. This check is performed at schema-load time, not at the
  record-validation level; a schema-level absence is not surfaced as `ValidationFailed`.

#### Bronze Layer Write

- **FR-017**: The system MUST write processed records to the Bronze layer partitioned by
  `dataset_id` and `ingestion_date`, serialised as Parquet files, against the storage
  endpoint — authenticated via Managed Identity.
- **FR-017a**: The Bronze write path MUST reuse the same storage client Singleton registered
  in the application's dependency injection container for input reads; a separate storage
  client for writes is not permitted.
- **FR-017b**: The Parquet schema MUST be derived from the vendor schema mapping passed as
  an explicit parameter to the writer — not from runtime reflection of record values, and not
  from a mapping injected at construction time. The writer MUST produce one Parquet column for
  each entry in the vendor schema mapping (keyed by `canonical_field`, typed by `data_type`),
  plus the five fixed enrichment columns (`site_id`, `timestamp`, `ingestion_time`,
  `source_dataset_id`, `schema_version`). A Parquet file containing only the five enrichment
  columns does NOT satisfy this requirement.
- **FR-018**: Processing MUST be idempotent with respect to `dataset_id`: re-processing the
  same event MUST atomically replace (not append to) the existing Bronze partition path for
  that dataset, producing an identical output.
- **FR-019**: The system MUST NOT write any records to the Bronze layer if the dataset fails
  data-quality validation or contains zero data rows (all-or-nothing write semantics per
  dataset).

#### Downstream Event Emission

- **FR-025**: Upon successful Bronze write, the system MUST publish a `dataset.bronze.available`
  integration event to the configured Service Bus **queue** (`SERVICEBUS_BRONZE_QUEUE_NAME`,
  default `dataset-bronze-available`) carrying: `dataset_id`, `record_count`, `bronze_path`
  (storage URI of the written partition), `schema_version`, and `correlation_id` propagated
  from the originating event.
- **FR-026**: The `dataset.bronze.available` event MUST NOT be published if the Bronze write
  fails, if data-quality validation rejects the dataset, or if the dataset is empty; partial
  or speculative events are prohibited.
- **FR-027**: Event naming MUST follow the medallion-layer progression convention:
  `dataset.{layer}.available` (e.g., `dataset.bronze.available` → `dataset.silver.available`),
  making each layer's availability self-describing and independently subscribable by downstream
  consumers.

#### Observability

- **FR-020**: Every function execution MUST emit structured log entries with a `CorrelationId`
  propagated from the triggering event's `correlation_id` field.
- **FR-021**: The system MUST emit the following custom metrics per execution:
  `records_processed`, `validation_pass_count`, `validation_fail_count`,
  `processing_duration_ms`, tagged with `dataset_id`. These metrics MUST be emitted from the
  processing handler (not only from the function entry point) so they are recorded even when
  the handler is invoked programmatically in tests.
- **FR-022**: The system MUST NOT use unstructured console output in any production code path.
- **FR-023**: Significant domain operations (CSV parsing, validation run, Bronze write) MUST
  each be wrapped in a named distributed-trace span so they appear as child spans in traces
  with per-operation timing and error tracking. All spans MUST originate from a single activity
  source defined in the domain layer; no duplicate source definitions are permitted across
  projects.
- **FR-024**: The pipeline MUST be deployed through a CI/CD pipeline that enforces a unit-test
  coverage gate (≥ 80% on domain and application layers), a Bicep what-if review stage, and
  a staging-slot health check before any production slot swap.

---

### Key Entities

- **DatasetAvailableEvent**: Integration event carrying `dataset_id`, `storage_path`
  (URI of the CSV file), `vendor_id`, `schema_version`, and `correlation_id` (optional —
  if absent the function generates a new `Guid`-based value; see FR-003); consumed from
  the `raw-energy-events` Service Bus queue.
- **RawRecord**: An unvalidated, vendor-named row parsed directly from the CSV file.
- **CanonicalRecord**: A validated, normalised, enriched record conforming to the platform's
  canonical data model; includes enrichment fields (`site_id`, `ingestion_time`,
  `source_dataset_id`, `schema_version`) plus all vendor-mapped canonical fields with units
  converted to platform standard.
- **VendorSchemaMapping**: Configuration artifact mapping a `vendor_id` + `schema_version`
  combination to: column-name translation rules, unit-conversion definitions, CSV field
  delimiter, expected file encoding (UTF-8), required field list, and per-field numeric range
  boundaries. Loaded at runtime from a blob storage schema registry.
- **ColumnMapping**: Per-column mapping record within `VendorSchemaMapping`, containing
  `vendor_column`, `canonical_field`, `data_type`, optional `unit_conversion`, optional
  `min_value` (numeric), and optional `max_value` (numeric). Range boundaries apply only to
  numeric fields; absent boundaries mean no range check for that field.
- **ValidationResult**: Value object encapsulating pass/fail status, per-record failure
  details, and aggregate counts for a single dataset processing run.
- **ProcessingMetrics**: Telemetry payload emitted at the end of each execution containing
  record counters and processing duration, emitted via the metrics API from within the
  processing handler.
- **DatasetBronzeAvailableEvent**: Integration event published after a successful Bronze write,
  carrying `dataset_id`, `record_count`, `bronze_path`, `schema_version`, and `correlation_id`;
  sent to the `dataset-bronze-available` Service Bus queue. Follows the medallion naming
  convention `dataset.{layer}.available`.

---

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Every well-formed `dataset.available` event results in corresponding records
  appearing in the Bronze layer within 60 seconds of event publication under normal load
  (single dataset, ≤ 10,000 rows).
- **SC-002**: Re-delivering the same `dataset.available` event produces no duplicate or
  additional records in the Bronze layer — idempotency verified across 100% of tested repeat
  deliveries.
- **SC-003**: 100% of datasets with data-quality failures (validation errors, empty files,
  unknown schemas, encoding issues) are routed to the dead-letter queue; zero partial or
  invalid records reach the Bronze layer.
- **SC-004**: Every processing execution (success or failure) produces at least one correlated
  distributed trace visible in the observability backend within 30 seconds, tagged with the
  correct service name, and containing `dataset_id` and processing outcome in span attributes
  — verifiable end-to-end across the host process and worker process.
- **SC-005**: The pipeline sustains processing of 50 concurrent dataset events without message
  loss, duplication, or timeout errors.
- **SC-006**: Schema normalisation correctly maps 100% of declared vendor column names to
  canonical fields for all vendor schemas registered in the schema registry. The resulting
  output contains all mapped canonical fields and correctly converted unit values — not only
  the 5 fixed enrichment columns.
- **SC-007**: Unit test coverage for domain validation and transformation logic is ≥ 80%; all
  critical paths (including unit conversion, range validation, empty-file rejection, and
  unknown-schema rejection) have an integration test verifiable against a local emulator.
- **SC-008**: Every successful Bronze write results in exactly one `dataset.bronze.available`
  event published on the message broker within the same logical processing transaction; no
  event is emitted for failed, rejected, or empty datasets.

---

## Clarifications

### Session 2026-03-15

- Q: How should processed records be written to the Fabric Lakehouse Bronze layer? → A: ADLS Gen2 / DataLake SDK — write Parquet files to the OneLake ADLS-compatible endpoint using `DataLakeServiceClient` (same SDK as input reads), authenticated via Managed Identity.
- Q: Should the pipeline emit a downstream event after successful Bronze write? → A: Yes — emit `dataset.bronze.available` on Service Bus (not a generic `dataset.processed`). Event naming follows the medallion-layer convention `dataset.{layer}.available`, making each layer's availability self-describing and independently subscribable.
- Q: How should non-standard CSV delimiters and non-UTF-8 encoded files be handled? → A: Delimiter is configurable per vendor schema mapping (comma, semicolon, or tab); non-UTF-8 files are rejected and dead-lettered with the detected encoding in the reason payload — no transcoding.

### Session 2026-03-23

- Q: Does this function cover Silver-layer processing (gap detection, quality metrics, ML features)? → A: No. This function covers raw → Bronze only. A separate function triggered by `dataset.bronze.available` is responsible for Bronze → Silver transformation.
- Q: Should the Parquet output include all canonical fields or only the enrichment metadata? → A: All canonical fields (FR-017b). Writing only the 5 fixed enrichment columns discards the transformed payload and is not acceptable.
- Q: Should the Service Bus queue names be hardcoded in the trigger? → A: No — both the inbound queue name and outbound queue name must be read from application settings (FR-001a, FR-025). The Service Bus tier in use is Basic — topics and subscriptions are not available; all triggers and publishers use queues.
- Q: How are numeric range boundaries defined for FR-010? → A: As optional `min_value` and `max_value` fields on each `ColumnMapping` entry in the vendor schema JSON. Fields without these properties are not range-validated.
- Q: How should an empty CSV (header only, zero data rows) be handled? → A: Dead-letter with reason `EmptyDataset` (FR-006c); no Bronze write, no downstream event.
- Q: How should the Parquet writer build its schema for dynamic canonical fields? → A: Build schema from the vendor schema mapping's column list — use each entry's `canonical_field` as the column name and `data_type` as the Parquet type, plus the five fixed enrichment columns; no runtime reflection of record field values.
- Q: What are the inner per-attempt retry parameters for transient storage failures (FR-005)? → A: 3 attempts, 2-second base delay, 30-second maximum delay (exponential back-off); exception propagated after exhaustion to allow Service Bus delivery count to increment.
- Q: How should local development provide a Service Bus endpoint for the trigger? → A: Official Microsoft Service Bus emulator as a Docker container; a `docker-compose.yml` at the service root MUST define both the storage emulator (Azurite) and the Service Bus emulator so `docker compose up` covers the full local environment with no cloud dependency. The local connection string uses `UseDevelopmentEmulator=true`. The trigger queue name (`raw-energy-events`) MUST match the queue name used by ingestion-func in both local and production environments.
- Q: What types should `min_value` and `max_value` use in `ColumnMapping`? → A: Decimal (or equivalent high-precision numeric) in code and JSON number — consistent with the `decimal` type used throughout the validator and transformer; avoids precision loss.
- Q: How should the vendor schema mapping reach the Bronze writer for Parquet schema construction? → A: Explicit parameter on `WriteAsync` — the handler passes the mapping it already holds at call time; the writer is stateless and independently testable.

### Session 2026-04-16

- Q: Where does the `site_id` value on each output record come from? → A: Extracted from the CSV column whose `VendorSchemaMapping` entry has `canonical_field: "site_id"`; a vendor schema missing this mapping is treated as unrecognised (UnknownSchema failure).
- Q: When the schema registry blob store is unavailable at runtime, how should the function respond? → A: Retry up to 3 attempts with the same exponential back-off as FR-005 (2 s base, 30 s max); after exhaustion propagate the exception and allow Service Bus delivery count to increment. A missing mapping (blob not found) is non-retriable and triggers UnknownSchema.
- Q: Should a missing `canonical_field: "timestamp"` mapping in `VendorSchemaMapping` trigger `UnknownSchema` (schema-level) or `ValidationFailed` (record-level)? → A: Schema-level — same treatment as `site_id` (FR-016a). If no `ColumnMapping` has `canonical_field: "timestamp"`, throw `UnknownSchemaException`; dead-letter with `error_type: "UnknownSchema"`. A schema-level absence is not surfaced as `ValidationFailed`.
- Q: What should happen when `correlation_id` is absent from `DatasetAvailableEvent`? → A: Optional with fallback — generate a new `Guid`-based `correlation_id` at the function trigger boundary, log a warning that no upstream correlation was provided, and proceed normally. The event is NOT dead-lettered for an absent `correlation_id` (FR-003).

### Session 2026-04-15

- Q: What unit conversions does FR-015 support? → A: Four keys: `kw_to_w` (× 1 000), `mw_to_w` (× 1 000 000), `kwh_to_wh` (× 1 000), `mwh_to_wh` (× 1 000 000). An unrecognised key is treated as absent (pass-through).
- Q: What is the `error_type` for an unrecognised vendor schema? → A: `"UnknownSchema"`. The dead-letter reason payload MUST include `vendor_id` and `schema_version` for operator diagnosis (FR-011).
- Q: How is idempotency (FR-018) implemented at the storage level? → A: The Bronze writer uses an atomic overwrite upload (not append) so that re-processing the same `dataset_id` replaces the partition in a single operation, leaving no intermediate state visible to downstream consumers.

---

## Assumptions

- The `dataset.available` event schema is stable and backward-compatible for the duration of
  this feature; breaking schema changes will be handled under a separate versioning initiative.
- The Fabric Lakehouse Bronze layer is accessible via the OneLake ADLS-compatible endpoint
  using the Azure Data Lake Storage Gen2 SDK with Managed Identity; the OneLake workspace
  and lakehouse item URLs are supplied as application settings.
- Vendor schema mappings (column name → canonical field, unit conversions, range boundaries)
  are stored as JSON blobs in a blob storage container (`schema-registry`) and loaded at
  runtime. For local development, the Azurite storage emulator with `UseDevelopmentStorage=true`
  is used for the schema registry blob store.
- "Valid numeric range" boundaries per field are optional properties (`min_value`, `max_value`)
  on the `ColumnMapping` record within the vendor schema JSON; absent boundaries mean no range
  check is applied for that field.
- A maximum of one CSV file is referenced per `dataset.available` event.
- The Bronze layer write is treated as an atomic replace of the entire dataset partition;
  row-level merges are out of scope.
- The hosting plan is a Premium tier that supports pre-warming to mitigate cold-start latency.
- For local development, the Service Bus trigger MUST be backed by the official Microsoft
  Service Bus emulator running as a Docker container alongside the storage emulator. The local
  settings file supplies `ServiceBusConnection` with `UseDevelopmentEmulator=true` pointing
  to the emulator. The trigger queue name (`raw-energy-events`) MUST match the queue name
  used by ingestion-func; both services share the same Service Bus namespace and queue in
  both local and production environments.
