# Data Model: Dataset Ingestion Pipeline (Raw → Bronze)

**Date**: 2026-03-24 | **Plan**: [plan.md](plan.md)

---

## Entities & Value Objects

### `VendorSchemaMapping` *(Configuration — loaded from Blob)*

| Field | Type | Nullable | Description |
| --- | --- | --- | --- |
| `VendorId` | `string` | No | Unique vendor identifier (e.g., `"vendor-abc"`) |
| `SchemaVersion` | `string` | No | Schema version string (e.g., `"v2"`) |
| `Delimiter` | `char` | No | CSV delimiter: `,`, `;`, or `\t` |
| `Encoding` | `string` | No | Must be `"UTF-8"` |
| `ColumnMappings` | `List<ColumnMapping>` | No | Ordered list of column translation rules |
| `RequiredFields` | `List<string>` | No | Vendor column names that must be non-null in every record |

**Identity**: `VendorId` + `SchemaVersion` (composite key; blob name: `{vendorId}-{schemaVersion}.json`)

---

### `ColumnMapping` *(Value Object — embedded in VendorSchemaMapping)*

| Field | Type | Nullable | Description |
| --- | --- | --- | --- |
| `VendorColumn` | `string` | No | Column name as it appears in the CSV header |
| `CanonicalField` | `string` | No | Platform canonical field name (e.g., `"power_w"`) |
| `DataType` | `string` | No | Type hint: `"string"`, `"decimal"`, `"double"`, `"float"`, `"int"`, `"integer"`, `"long"`, `"bool"`, `"boolean"`, `"datetime"` |
| `UnitConversion` | `string?` | Yes | Conversion key: `"kw_to_w"`, `"mw_to_w"`, `"kwh_to_wh"`, `"mwh_to_wh"`, or null |
| `MinValue` | `decimal?` | Yes | Lower bound for numeric range validation (FR-010); null = no lower bound |
| `MaxValue` | `decimal?` | Yes | Upper bound for numeric range validation (FR-010); null = no upper bound |

**Constraints**:

- `MinValue` and `MaxValue` are only evaluated when `DataType` is `"decimal"`, `"double"`, or `"float"`.
- If both are set, `MinValue` ≤ `MaxValue` must hold (validated at schema load time).

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
| `SiteId` | `string` | No | Derived from the `site_id` canonical field mapping |
| `Timestamp` | `DateTimeOffset` | No | UTC normalised timestamp field |
| `IngestionTime` | `DateTimeOffset` | No | UTC timestamp of processing (set by `RecordEnricher`) |
| `SourceDatasetId` | `string` | No | Dataset identifier from the triggering event |
| `SchemaVersion` | `string` | No | Schema version from `VendorSchemaMapping` |
| `Fields` | `IReadOnlyDictionary<string, object>` | No | All canonical field values including vendor-mapped fields, keyed by canonical field name |

**Constraints**: Immutable record type (`record`). `Fields` includes all entries from `ColumnMappings` plus the enrichment fields.

---

### `ValidationResult` *(Value Object)*

| Field | Type | Description |
| --- | --- | --- |
| `IsSuccess` | `bool` | True only when `FailCount == 0` |
| `PassCount` | `int` | Number of records that passed all validation rules |
| `FailCount` | `int` | Number of records (or schema-level checks) that failed |
| `Failures` | `IReadOnlyList<ValidationFailure>` | Ordered list of per-rule failure details |

**State transitions**: Created by `DataQualityValidator.Validate()`; never mutated after creation.

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

| Field | Type | Description |
| --- | --- | --- |
| `DatasetId` | `string` | Dataset identifier tag |
| `RecordsProcessed` | `int` | Total records written to Bronze |
| `ValidationPassCount` | `int` | Records passing all validation rules |
| `ValidationFailCount` | `int` | Records or schema checks failing validation |
| `ProcessingDurationMs` | `long` | Wall-clock duration from event receipt to Bronze write completion |

**Note**: Not a persisted entity — emitted as OTel metric instruments from `ProcessingMetricsEmitter`. Each field maps to a named OTel counter or histogram instrument.

---

### `DatasetId` *(Value Object)*

| Field | Type | Description |
| --- | --- | --- |
| `Value` | `string` | Non-empty string; uniquely identifies a dataset ingestion event |

**Constraints**: Must be non-null and non-whitespace. Equality is string equality on `Value`.

---

## Integration Events (published / consumed)

### `DatasetAvailableEvent` *(consumed — inbound)*

| Field | Type | Required | Description |
| --- | --- | --- | --- |
| `DatasetId` | `string` | Yes | Unique dataset identifier |
| `StoragePath` | `string` | Yes | ADLS URI of the CSV file (`abfss://` or `http://` for local) |
| `VendorId` | `string` | Yes | Vendor identifier; used to look up `VendorSchemaMapping` |
| `SchemaVersion` | `string` | Yes | Schema version; combined with `VendorId` to select mapping |
| `CorrelationId` | `string` | Yes | W3C `traceparent`-compatible correlation ID for distributed tracing |
| `PublishedAt` | `DateTimeOffset` | Yes | Publication timestamp (UTC) |

**Validation**: If `DatasetId` or `StoragePath` is null/empty, dead-letter immediately (FR-003).

---

### `DatasetBronzeAvailableEvent` *(published — outbound)*

| Field | Type | Description |
| --- | --- | --- |
| `dataset_id` | `string` | Matches `DatasetAvailableEvent.DatasetId` |
| `record_count` | `int` | Number of records written to Bronze |
| `bronze_path` | `string` | ADLS URI of the written Parquet partition |
| `schema_version` | `string` | Schema version used during processing |
| `correlation_id` | `string` | Propagated from `DatasetAvailableEvent.CorrelationId` |
| `published_at` | `string` | ISO 8601 UTC publication timestamp |

**Published only on successful Bronze write** (FR-026). JSON serialised with snake_case keys.

---

## Parquet Output Schema

The Bronze Parquet file schema is assembled at write time from two sources:

### Fixed Enrichment Columns (always present, non-nullable)

| Column | Parquet Type | .NET Type |
| --- | --- | --- |
| `site_id` | `BYTE_ARRAY` (UTF-8) | `string` |
| `timestamp` | `TIMESTAMP_MICROS` | `DateTimeOffset` |
| `ingestion_time` | `TIMESTAMP_MICROS` | `DateTimeOffset` |
| `source_dataset_id` | `BYTE_ARRAY` (UTF-8) | `string` |
| `schema_version` | `BYTE_ARRAY` (UTF-8) | `string` |

### Vendor-Mapped Columns (dynamic — one per `ColumnMapping` entry, nullable)

| `DataType` value | Parquet Type | .NET DataField type |
| --- | --- | --- |
| `datetime` | `TIMESTAMP_MICROS` | `DataField<DateTimeOffset?>` |
| `decimal`, `double`, `float` | `DECIMAL` / `DOUBLE` | `DataField<decimal?>` |
| `int`, `integer`, `long` | `INT64` | `DataField<long?>` |
| `bool`, `boolean` | `BOOLEAN` | `DataField<bool?>` |
| `string` (default) | `BYTE_ARRAY` (UTF-8) | `DataField<string>` |

Vendor-mapped columns that duplicate an enrichment column name (e.g., a mapping with `canonical_field: "site_id"`) are **skipped** in the dynamic section — the enrichment column takes precedence.

---

## State Transitions

```mermaid
DatasetAvailableEvent received
        │
        ▼
[Deserialize] ──fail──► DeadLetter (DeserializationFailed)
        │
        ▼
[Load VendorSchemaMapping] ──not found──► Return Fail (no schema)
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
[SchemaTransformer + RecordEnricher]
        │
        ▼
[OneLakeBronzeWriter.WriteAsync] ──exception──► re-throw → SB retry
        │
        ▼
[Publish dataset.bronze.available]
        │
        ▼
[CompleteMessage]
```
