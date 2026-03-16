# Feature Specification: Dataset Ingestion Pipeline

**Feature Branch**: `001-dataset-ingestion-pipeline`
**Created**: 2026-03-15
**Status**: Draft
**Input**: User description: "IT WILL create .net based azure isolated functions. The processing function consumes a dataset.available event and performs the following operations: Retrieve the dataset from the referenced ADLS storage path. Parse the CSV file into structured records, detecting headers and converting fields to appropriate data types. Validate data quality, including checks for required fields (e.g., timestamp), valid numeric ranges, and basic schema consistency. Normalize and transform the schema from vendor-specific column names to the platform's canonical data model. Apply lightweight enrichment, such as standardizing timestamps, units, and metadata (e.g., site_id, ingestion_time). Write the processed records to the Fabric Lakehouse Bronze layer, ensuring idempotent processing using the dataset identifier. Emit processing metrics and logs for observability and error tracking."

---

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Successful End-to-End Dataset Processing (Priority: P1)

A vendor dataset arrives in ADLS storage and a `dataset.available` event is published on the
message broker. The pipeline automatically picks up the event, retrieves the file, parses and
validates the CSV, transforms the records to the canonical data model, enriches them with
platform metadata, and persists the structured records to the Bronze layer — all without
operator intervention.

**Why this priority**: This is the core pipeline flow. Without it no downstream analytics
can be fed, making it the single most valuable piece of end-to-end behaviour.

**Independent Test**: Can be fully tested by publishing a synthetic `dataset.available` event
referencing a well-formed CSV fixture in a local storage emulator and asserting that matching
records appear in the Bronze output store with correct field mappings and enrichment metadata.

**Acceptance Scenarios**:

1. **Given** a valid `dataset.available` event referencing an ADLS path containing a
   well-formed CSV with recognised vendor headers, **When** the function processes the event,
   **Then** all CSV rows are parsed, validated, transformed to the canonical model, enriched
   with `site_id` and `ingestion_time`, and written to the Bronze layer under the correct
   partition keyed by the dataset identifier.

2. **Given** the same `dataset.available` event is delivered twice (duplicate delivery),
   **When** the function processes the second delivery, **Then** no duplicate records are
   written to the Bronze layer — the output is identical to that of the first delivery.

3. **Given** a valid event, **When** processing completes successfully, **Then** structured
   telemetry is emitted containing dataset identifier, record count, processing duration,
   validation pass/fail counts, and zero error entries in the dead-letter queue — **and** a
   `dataset.bronze.available` event is published on Service Bus carrying `dataset_id`,
   `record_count`, `bronze_path`, and the propagated `correlation_id`.

---

### User Story 2 - Data Quality Failure Handling (Priority: P2)

A vendor CSV file fails one or more data-quality checks (missing required `timestamp` field,
out-of-range numeric values, or unrecognised schema). The pipeline detects, records, and
routes the failure without corrupting the Bronze layer or silently discarding the message.

**Why this priority**: Silent data corruption downstream is worse than a visible failure.
Reliable rejection with full observability is a prerequisite for production trust.

**Independent Test**: Can be fully tested by publishing an event referencing a CSV fixture
with deliberately invalid records (null timestamps, out-of-range values) and asserting that
the message lands on the dead-letter queue, no partial records reach the Bronze layer, and
a structured validation-failure log entry is emitted.

**Acceptance Scenarios**:

1. **Given** a CSV file where one or more records are missing the required `timestamp` field,
   **When** the function runs data-quality validation, **Then** the affected records are
   rejected, a structured error log entry identifying the failing rows and rule is emitted,
   and the message is dead-lettered after exhausting the configured delivery count.

2. **Given** a CSV file where numeric fields contain values outside the configured valid
   range, **When** validation runs, **Then** those records are flagged, the dataset fails
   quality gates, and the event is routed to the dead-letter queue with a structured reason
   payload.

3. **Given** a CSV file whose header row does not match any known vendor-schema mapping,
   **When** the function attempts schema normalisation, **Then** processing halts, an
   unknown-schema error is emitted as structured telemetry, and the message is dead-lettered.

---

### User Story 3 - Observability and Metrics Emission (Priority: P3)

Platform operators and data engineers can observe the health of the ingestion pipeline in
near-real time through structured logs and metrics — without needing access to raw function
logs.

**Why this priority**: Observability is a production-readiness requirement; without it
silent failures cannot be detected.

**Independent Test**: Can be fully tested by processing a batch of synthetic events and
querying Application Insights for the expected custom metrics (`records_processed`,
`validation_failures`, `processing_duration_ms`) and correlated log entries sharing a
`CorrelationId`.

**Acceptance Scenarios**:

1. **Given** a successfully processed dataset, **When** the function emits telemetry,
   **Then** Application Insights records a custom metric entry containing `dataset_id`,
   `record_count`, `validation_pass_count`, `validation_fail_count`, and
   `processing_duration_ms` correlated by a single `CorrelationId`.

2. **Given** a failed dataset processing attempt, **When** the error is logged, **Then** the
   structured log entry contains `dataset_id`, `error_type`, `error_detail`, and the same
   `CorrelationId` as the triggering event, enabling end-to-end trace reconstruction.

---

### Edge Cases

- What happens when the ADLS file referenced in the event no longer exists or is inaccessible
  at processing time (transient auth failure vs permanently missing file)?
- How does the system handle a CSV file that is empty (header row only, zero data rows)?
- What happens when a partially written Bronze output exists from a previously interrupted
  run for the same dataset identifier?
- Non-UTF-8 encoded files are rejected and dead-lettered with the detected encoding in the
  reason payload; transcoding is not performed (FR-006b). Mixed line endings (`\r\n` / `\n`)
  are normalised by the CSV parser and do not cause rejection.
- What happens when the Bronze layer write fails mid-batch — are partial writes visible?
- How does the function behave when the `dataset.available` event payload is malformed or
  missing the ADLS path field?

---

## Requirements *(mandatory)*

### Functional Requirements

#### Event Consumption

- **FR-001**: The system MUST trigger processing exclusively from a `dataset.available`
  integration event delivered via the configured Service Bus topic subscription; polling or
  manual triggers are not permitted.
- **FR-002**: The system MUST extract the ADLS storage path and dataset identifier from the
  `dataset.available` event payload before initiating any downstream processing.
- **FR-003**: The system MUST dead-letter any event whose payload is malformed or missing
  mandatory fields (`dataset_id`, `storage_path`) after a single delivery attempt with a
  structured reason payload.

#### Dataset Retrieval

- **FR-004**: The system MUST retrieve the CSV file from the ADLS path specified in the
  event using Managed Identity; shared-key connection strings are prohibited.
- **FR-005**: The system MUST treat a missing or inaccessible file as a transient failure,
  applying exponential back-off retry before dead-lettering the event after the configured
  maximum delivery count.

#### CSV Parsing

- **FR-006**: The system MUST auto-detect the presence and position of a CSV header row and
  use it to map columns to named fields.
- **FR-006a**: The CSV field delimiter MUST be read from the `VendorSchemaMapping` for the
  matching `vendor_id` + `schema_version`; the delimiter is not assumed to be a comma.
  Supported delimiters include comma (`,`), semicolon (`;`), and tab (`\t`).
- **FR-006b**: The system MUST verify that the CSV file is UTF-8 encoded before parsing;
  files with a non-UTF-8 encoding MUST be rejected and dead-lettered with a structured reason
  payload identifying the detected encoding — transcoding is prohibited.
- **FR-007**: The system MUST convert CSV field values to appropriate data types (integer,
  decimal, datetime, string) based on the canonical schema definition; unparseable values
  MUST be recorded as validation failures.
- **FR-008**: The system MUST handle CSV files with at least 10,000 rows without exceeding
  the function's configured timeout.

#### Data Quality Validation

- **FR-009**: The system MUST validate that every record contains a non-null, parseable
  `timestamp` field; records failing this check MUST be rejected.
- **FR-010**: The system MUST validate that all numeric fields declared as required in the
  canonical schema fall within their configured valid ranges; out-of-range records MUST be
  rejected.
- **FR-011**: The system MUST verify that the CSV header schema matches a known
  vendor-to-canonical mapping; files with an unrecognised schema MUST cause the entire
  dataset to fail (no partial acceptance).
- **FR-012**: The system MUST emit a structured validation summary (pass count, fail count,
  failure reasons per rule) as telemetry regardless of overall outcome.

#### Schema Normalisation & Enrichment

- **FR-013**: The system MUST map vendor-specific column names to the platform's canonical
  field names using a configurable vendor schema registry; hard-coded column name mappings
  are prohibited.
- **FR-014**: The system MUST standardise all datetime fields to ISO 8601 UTC format during
  transformation.
- **FR-015**: The system MUST standardise numeric unit values (e.g., kW → W, °F → °C) where
  unit metadata is present in the vendor schema definition.
- **FR-016**: The system MUST attach the following enrichment fields to every output record:
  `site_id` (derived from the dataset identifier), `ingestion_time` (UTC timestamp of
  processing), `source_dataset_id`, and `schema_version`.

#### Downstream Event Emission

- **FR-025**: Upon successful Bronze write, the system MUST publish a `dataset.bronze.available`
  integration event on the configured Service Bus topic carrying: `dataset_id`, `record_count`,
  `bronze_path` (ADLS URI of the written partition), `schema_version`, and `correlation_id`
  propagated from the originating `dataset.available` event.
- **FR-026**: The `dataset.bronze.available` event MUST NOT be published if the Bronze write
  fails or if data-quality validation rejects the dataset; partial or speculative events are
  prohibited.
- **FR-027**: Event naming MUST follow the medallion-layer progression convention:
  `dataset.{layer}.available` (e.g., `dataset.bronze.available` → `dataset.silver.available`
  → `dataset.gold.available`), making each layer's availability self-describing and
  independently subscribable by downstream consumers.

#### Bronze Layer Write

- **FR-017**: The system MUST write processed records to the Fabric Lakehouse Bronze layer
  partitioned by `dataset_id` and `ingestion_date`, serialised as Parquet files, using the
  Azure Data Lake Storage Gen2 SDK (`DataLakeServiceClient`) against the OneLake
  ADLS-compatible endpoint — authenticated via Managed Identity (`DefaultAzureCredential`).
- **FR-017a**: The Parquet write path MUST reuse the same `DataLakeServiceClient` Singleton
  registered in DI for input reads; no separate storage SDK is permitted for Bronze writes.
- **FR-018**: Processing MUST be idempotent with respect to `dataset_id`: re-processing the
  same event MUST overwrite (atomic directory replace, not append) the existing Bronze
  partition path for that dataset, producing an identical output.
- **FR-019**: The system MUST NOT write any records to the Bronze layer if the dataset fails
  data-quality validation (all-or-nothing write semantics per dataset).

#### Observability

- **FR-020**: Every function execution MUST emit structured log entries with a `CorrelationId`
  propagated from the triggering event via W3C `traceparent` trace context.
- **FR-021**: The system MUST emit the following custom metrics per execution via the
  OpenTelemetry Metrics API: `records_processed`, `validation_pass_count`,
  `validation_fail_count`, `processing_duration_ms`, `dataset_id`.
- **FR-022**: The system MUST NOT use `Console.WriteLine`, `Debug.WriteLine`, or unstructured
  logging in any production code path.
- **FR-023**: Significant domain operations (CSV parsing, validation run, Bronze write) MUST
  each be wrapped in a named OpenTelemetry Activity span so they appear as child spans in
  distributed traces with per-operation timing and error tracking.
- **FR-024**: The pipeline MUST be deployed through a CI/CD pipeline that enforces a unit-test
  coverage gate (≥ 80% on Domain and Application layers), a Bicep what-if review stage, and
  a staging-slot health check before any production slot swap.

### Key Entities

- **DatasetAvailableEvent**: Integration event carrying `dataset_id`, `storage_path` (ADLS URI),
  `vendor_id`, `schema_version`, and `correlation_id`; published by an upstream producer.
- **RawRecord**: An unvalidated, vendor-named row parsed directly from the CSV file.
- **CanonicalRecord**: A validated, normalised, enriched record conforming to the platform's
  canonical data model; includes enrichment fields (`site_id`, `ingestion_time`,
  `source_dataset_id`, `schema_version`).
- **VendorSchemaMapping**: Configuration artifact mapping a `vendor_id` + `schema_version`
  combination to: column-name translation rules, unit-conversion definitions, CSV field
  delimiter (`,`, `;`, or `\t`), and expected file encoding (must be UTF-8).
- **ValidationResult**: Value object encapsulating pass/fail status, per-record failure details,
  and aggregate counts for a single dataset processing run.
- **ProcessingMetrics**: Telemetry payload emitted at the end of each execution containing record
  counters and processing duration.
- **DatasetBronzeAvailableEvent**: Integration event published after a successful Bronze write,
  carrying `dataset_id`, `record_count`, `bronze_path`, `schema_version`, and `correlation_id`;
  consumed by downstream Silver-layer processors. Follows the medallion naming convention
  `dataset.{layer}.available`.

---

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Every well-formed `dataset.available` event results in corresponding records
  appearing in the Bronze layer within 60 seconds of event publication under normal load
  (single dataset, ≤ 10,000 rows).
- **SC-002**: Re-delivering the same `dataset.available` event produces no duplicate or
  additional records in the Bronze layer — idempotency verified across 100% of tested repeat
  deliveries.
- **SC-003**: 100% of datasets with data-quality failures are routed to the dead-letter queue;
  zero partial or invalid records reach the Bronze layer.
- **SC-004**: Every processing execution (success or failure) produces at least one correlated
  OpenTelemetry trace visible in Application Insights within 30 seconds, tagged with the
  correct `OTEL_SERVICE_NAME`, and containing `dataset_id` and processing outcome in span
  attributes — verifiable end-to-end across the host process and worker process.
- **SC-005**: The pipeline sustains processing of 50 concurrent dataset events without message
  loss, duplication, or timeout errors.
- **SC-006**: Schema normalisation correctly maps 100% of declared vendor column names to
  canonical fields for all vendor schemas registered in the schema registry.
- **SC-007**: Unit test coverage for domain validation and transformation logic is ≥ 80%; all
  critical paths have an integration test verifiable against a local emulator.
- **SC-008**: Every successful Bronze write results in exactly one `dataset.bronze.available`
  event published on Service Bus within the same logical processing transaction; no event is
  emitted for failed or rejected datasets.

---

## Clarifications

### Session 2026-03-15

- Q: How should processed records be written to the Fabric Lakehouse Bronze layer? → A: ADLS Gen2 / DataLake SDK — write Parquet files to the OneLake ADLS-compatible endpoint using `DataLakeServiceClient` (same SDK as input reads), authenticated via Managed Identity.
- Q: Should the pipeline emit a downstream event after successful Bronze write? → A: Yes — emit `dataset.bronze.available` on Service Bus (not a generic `dataset.processed`). Event naming follows the medallion-layer convention `dataset.{layer}.available`, making each layer's availability self-describing and independently subscribable.
- Q: How should non-standard CSV delimiters and non-UTF-8 encoded files be handled? → A: Delimiter is configurable per vendor schema mapping (comma, semicolon, or tab); non-UTF-8 files are rejected and dead-lettered with the detected encoding in the reason payload — no transcoding.

---

## Assumptions

- The `dataset.available` event schema is stable and backward-compatible for the duration of
  this feature; breaking schema changes will be handled under a separate versioning initiative.
- The Fabric Lakehouse Bronze layer is accessible via the OneLake ADLS-compatible endpoint
  using the Azure Data Lake Storage Gen2 SDK (`DataLakeServiceClient`) with Managed Identity;
  the OneLake workspace and lakehouse item URLs are supplied as application settings.
- Vendor schema mappings (column name → canonical field, unit conversions) are externally
  configurable (e.g., stored in a Blob container or configuration service) rather than
  compiled into the function binary; the exact store is deferred to planning.
- "Valid numeric range" boundaries per field are defined in the vendor schema mapping
  configuration, not hard-coded.
- A maximum of one CSV file is referenced per `dataset.available` event.
- The Bronze layer write is treated as an atomic replace of the entire dataset partition;
  row-level merges are out of scope.
- The hosting plan is Azure Functions Premium with a Warmup trigger to mitigate cold-start
  latency, consistent with the project constitution.
