# Data Model: Dataset Ingestion Pipeline (Raw → Bronze)

**Date**: 2026-04-16 | **Plan**: [plan.md](plan.md)

---

## Entities & Value Objects

### `VendorSchemaMapping` *(Configuration — loaded from Blob)*

| Field | Type | Nullable | Description |
| --- | --- | --- | --- |
| `VendorId` | `string` | No | Unique vendor identifier (e.g., `"vendor-abc"`) |
| `SchemaVersion` | `string` | No | Schema version string (e.g., `"v2"`) |
| `Delimiter` | `char` | No | CSV delimiter: `,`, `;`, or `\t` |
| `Encoding` | `string` | No | Must be `"UTF-8"` |
| `ColumnMappings` | `List<ColumnMapping>` | No | Ordered list of column translation rules. Must contain at least one entry with `CanonicalField = "site_id"` and at least one with `CanonicalField = "timestamp"` (both validated at load time — see FR-016, FR-016a) |
| `RequiredFields` | `List<string>` | No | Vendor column names that must be non-null in every record |

**Identity**: `VendorId` + `SchemaVersion` (composite key; blob name: `{vendorId}-{schemaVersion}.json`)

**Invariants**:

- If no `ColumnMapping` has `CanonicalField == "site_id"` (case-insensitive), `BlobSchemaRegistry` throws `UnknownSchemaException` before returning the mapping (FR-016).
- If no `ColumnMapping` has `CanonicalField == "timestamp"` (case-insensitive), `BlobSchemaRegistry` throws `UnknownSchemaException` before returning the mapping (FR-016a). A missing `timestamp` mapping is a schema-level defect, not a per-record data-quality failure.

---

### `ColumnMapping` *(Value Object — embedded in VendorSchemaMapping)*

| Field | Type | Nullable | Description |
| --- | --- | --- | --- |
| `VendorColumn` | `string` | No | Column name as it appears in the CSV header |
| `CanonicalField` | `string` | No | Platform canonical field name (e.g., `"power_w"`, `"site_id"`) |
| `DataType` | `string` | No | Type hint: `"string"`, `"decimal"`, `"double"`, `"float"`, `"int"`, `"integer"`, `"long"`, `"bool"`, `"boolean"`, `"datetime"` |
| `UnitConversion` | `string?` | Yes | Conversion key: `"kw_to_w"`, `"mw_to_w"`, `"kwh_to_wh"`, `"mwh_to_wh"`, or null |
| `MinValue` | `decimal?` | Yes | Lower bound for numeric range validation (FR-010); null = no lower bound |
| `MaxValue` | `decimal?` | Yes | Upper bound for numeric range validation (FR-010); null = no upper bound |

**Constraints**:

- `MinValue` and `MaxValue` are only evaluated when `DataType` is `"decimal"`, `"double"`, or `"float"`.
- If both are set, `MinValue ≤ MaxValue` must hold (validated at schema load time).
- `UnitConversion` is only applied to numeric fields; an unrecognised key is treated as absent (pass-through).
- Supported unit conversions: `kw_to_w` (× 1 000), `mw_to_w` (× 1 000 000), `kwh_to_wh` (× 1 000), `mwh_to_wh` (× 1 000 000).

---

### `RawRecord` *(Value Object — parsed from CSV)*

| Field | Type | Description |
| --- | --- | --- |
| `RowIndex` | `int` | 1-based row index within the CSV (used in validation failure messages) |
| `Fields` | `IReadOnlyDictionary<string, string>` | Raw string values keyed by vendor column name (case-insensitive) |

**Lifecycle**: Created by `CsvParserService`; consumed by `DataQualityValidator` and `SchemaTransformer`. Never persisted.

---

### `CanonicalRecord` *(Value Object — transformed output)*

| Field | Type | Nullable | Description |
| --- | --- | --- | --- |
| `SiteId` | `string` | No | Promoted from `Fields["site_id"]` by `RecordEnricher`; value originates from the CSV column mapped to `canonical_field: "site_id"` in `VendorSchemaMapping` |
| `Timestamp` | `DateTimeOffset` | No | UTC normalised timestamp field |
| `IngestionTime` | `DateTimeOffset` | No | UTC timestamp of processing (set by `RecordEnricher`) |
| `SourceDatasetId` | `string` | No | Dataset identifier from the triggering event |
| `SchemaVersion` | `string` | No | Schema version from `VendorSchemaMapping` |
| `Fields` | `IReadOnlyDictionary<string, object>` | No | All canonical field values (vendor-mapped + unit-converted), keyed by canonical field name |

**Constraints**: Immutable record type. `Fields` includes all entries from `ColumnMappings` with unit conversions applied, plus the enrichment fields. `SiteId` must not be null or empty — a missing `site_id` mapping triggers `UnknownSchemaException` at schema load time before any record is created.

---

### `ValidationResult` *(Value Object)*

| Field | Type | Description |
| --- | --- | --- |
| `IsSuccess` | `bool` | True only when `FailCount == 0` |
| `PassCount` | `int` | Number of records that passed all validation rules |
| `FailCount` | `int` | Number of records (or schema-level checks) that failed |
| `Failures` | `IReadOnlyList<ValidationFailure>` | Ordered list of per-rule failure details |

---

### `ValidationFailure` *(Value Object — embedded in ValidationResult)*

| Field | Type | Description |
| --- | --- | --- |
| `RowIndex` | `int` | 0 for schema-level failures; 1-based row index for record-level failures |
| `FieldName` | `string` | Vendor column name the failure relates to |
| `Rule` | `string` | Rule identifier: `"UnknownSchema"`, `"RequiredField"`, `"NumericRange"`, `"NumericParse"` |
| `Detail` | `string` | Human-readable description of the failure |

---

### `ProcessingMetrics` *(Telemetry — emitted via OTel Meter)*

| Field | Type | OTel Instrument | Description |
| --- | --- | --- | --- |
| `DatasetId` | `string` | Tag on all instruments | Dataset identifier |
| `RecordsProcessed` | `int` | `Counter<long>` | Total records written to Bronze |
| `ValidationPassCount` | `int` | `Counter<long>` | Records passing all validation rules |
| `ValidationFailCount` | `int` | `Counter<long>` | Records or schema checks failing validation |
| `ProcessingDurationMs` | `long` | `Histogram<long>` | Wall-clock ms from event receipt to Bronze write completion |

**Note**: Not a persisted entity — emitted from `ProcessingMetricsEmitter` called by `ProcessDatasetCommandHandler`.

---

### `DatasetId` *(Value Object)*

| Field | Type | Description |
| --- | --- | --- |
| `Value` | `string` | Non-empty string; uniquely identifies a dataset ingestion event |

**Constraints**: Must be non-null and non-whitespace. Equality is string equality on `Value`.

---

## Integration Events

### `DatasetAvailableEvent` *(consumed — inbound, from `raw-energy-events` queue)*

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| `DatasetId` | `string` | Yes | Unique dataset identifier; used as Bronze partition key |
| `StoragePath` | `string` | Yes | Storage URI of the CSV file |
| `VendorId` | `string` | Yes | Vendor identifier; used to look up `VendorSchemaMapping` |
| `SchemaVersion` | `string` | Yes | Schema version; combined with `VendorId` to select mapping |
| `CorrelationId` | `string` | No (optional) | W3C `traceparent`-compatible correlation ID. If absent, the function generates a new `Guid`-based value and logs a warning — the event is NOT dead-lettered (FR-003). |
| `PublishedAt` | `DateTimeOffset` | Yes | Publication timestamp (UTC) |

**Validation**: If `DatasetId` or `StoragePath` is null/empty, dead-letter immediately (FR-003). `CorrelationId` is optional — absence triggers a Guid fallback, not a dead-letter.

---

### `DatasetBronzeAvailableEvent` *(published — outbound, to `dataset-bronze-available` queue)*

| Field | Type | Description |
| --- | --- | --- |
| `dataset_id` | `string` | Matches `DatasetAvailableEvent.DatasetId` |
| `record_count` | `int` | Number of records written to Bronze |
| `bronze_path` | `string` | Storage URI of the written Parquet partition |
| `schema_version` | `string` | Schema version used during processing |
| `correlation_id` | `string` | Propagated from `DatasetAvailableEvent.CorrelationId` |
| `published_at` | `string` | ISO 8601 UTC publication timestamp |

**Published only on successful Bronze write** (FR-026). JSON serialised with snake_case keys.

---

## Parquet Output Schema

### Fixed Enrichment Columns (always present, non-nullable)

| Column | Parquet Type | .NET Type | Source |
| --- | --- | --- | --- |
| `site_id` | `BYTE_ARRAY` (UTF-8) | `string` | CSV column with `canonical_field: "site_id"` |
| `timestamp` | `TIMESTAMP` (INT64 millis) | `DateTime` | CSV column with `canonical_field: "timestamp"` |
| `ingestion_time` | `TIMESTAMP` (INT64 millis) | `DateTime` | Set by `RecordEnricher` at processing time |
| `source_dataset_id` | `BYTE_ARRAY` (UTF-8) | `string` | `DatasetAvailableEvent.DatasetId` |
| `schema_version` | `BYTE_ARRAY` (UTF-8) | `string` | `VendorSchemaMapping.SchemaVersion` |

### Vendor-Mapped Columns (dynamic — one per `ColumnMapping` entry, nullable)

| `DataType` value | Parquet Type | .NET DataField type |
| --- | --- | --- |
| `datetime` | `TIMESTAMP` (INT64 millis) | `DateTimeDataField(isNullable: true)` |
| `decimal` | `DECIMAL(18,6)` | `DecimalDataField(precision:18, scale:6, isNullable:true)` |
| `double`, `float` | `DOUBLE` | `DataField<double?>`/`DataField<float?>` |
| `int`, `integer` | `INT32` | `DataField<int?>` |
| `long` | `INT64` | `DataField<long?>` |
| `bool`, `boolean` | `BOOLEAN` | `DataField<bool?>` |
| `string` (default) | `BYTE_ARRAY` (UTF-8) | `DataField<string>` |

Vendor-mapped columns with `canonical_field` matching an enrichment column name are **skipped** in the dynamic section — the enrichment column takes precedence.

---

## Exceptions

| Exception | Properties | Thrown by | Caught by |
| --- | --- | --- | --- |
| `EmptyDatasetException` | message | `CsvParserService` | `ProcessDatasetFunction` → DLQ `EmptyDataset` |
| `UnsupportedEncodingException` | `DetectedEncoding` | `CsvParserService` | `ProcessDatasetFunction` → DLQ `UnsupportedEncoding` |
| `UnknownSchemaException` | `VendorId`, `SchemaVersion` | `BlobSchemaRegistry` | `ProcessDatasetFunction` → DLQ `UnknownSchema` |
| `DatasetValidationException` | `ValidationResult` | `ProcessDatasetCommandHandler` | `ProcessDatasetFunction` → DLQ `ValidationFailed` |

---

## State Transitions

```text
DatasetAvailableEvent received (raw-energy-events queue)
        │
        ▼
[Deserialize] ──fail──► DeadLetter (DeserializationFailed)
        │
        ▼
[CorrelationId present?] ──no──► generate Guid; log warning; continue
        │
        ▼
[Load VendorSchemaMapping from BlobSchemaRegistry]
        │──transient error──► Retry ×2 ──exhausted──► re-throw → SB retry
        │──404 (not found)──► throw UnknownSchemaException → DeadLetter (UnknownSchema)
        │──no site_id mapping──► throw UnknownSchemaException → DeadLetter (UnknownSchema)
        │──no timestamp mapping──► throw UnknownSchemaException → DeadLetter (UnknownSchema)
        │
        ▼
[Read CSV from ADLS] ──transient error──► Retry ×2 ──exhausted──► re-throw → SB retry
        │             ──permanent 404──► re-throw → SB retry
        ▼
[Parse CSV / UTF-8 check] ──non-UTF-8──► throw UnsupportedEncodingException → DeadLetter
        │                 ──empty CSV──► throw EmptyDatasetException → DeadLetter (EmptyDataset)
        ▼
[DataQualityValidator] ──failures──► throw DatasetValidationException → DeadLetter (ValidationFailed)
        │
        ▼
[SchemaTransformer (unit conversion) + RecordEnricher (site_id promotion)]
        │
        ▼
[OneLakeBronzeWriter.WriteAsync — UploadAsync(overwrite:true)]
        │──exception──► re-throw → SB retry
        ▼
[Publish dataset.bronze.available to dataset-bronze-available queue]
        │
        ▼
[CompleteMessage]
```
