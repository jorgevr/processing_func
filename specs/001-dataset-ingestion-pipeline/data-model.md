# Data Model: Dataset Ingestion Pipeline

**Branch**: `001-dataset-ingestion-pipeline` | **Date**: 2026-03-15

---

## Domain Layer

### Aggregate: `DatasetProcessingJob`

Represents a single end-to-end processing run for one `dataset.available` event. Enforces
the invariant that a Bronze write and downstream event only occur after a fully valid,
transformed dataset.

```
DatasetProcessingJob
├── DatasetId          : DatasetId          (value object, immutable identity)
├── StoragePath        : Uri                (ADLS input path)
├── VendorId           : string
├── SchemaVersion      : string
├── CorrelationId      : string             (W3C traceparent propagated from event)
├── Status             : ProcessingStatus   (enum: Received | Validating | Transforming | Writing | Completed | Failed)
├── ValidationResult   : ValidationResult   (value object, set after validation)
└── Metrics            : ProcessingMetrics  (value object, set on completion)
```

**Invariants**:
- `WriteTooBronze()` MUST NOT be called unless `ValidationResult.IsSuccess == true`
- `EmitDownstreamEvent()` MUST NOT be called unless `Status == Completed`

---

### Value Object: `DatasetId`

```
DatasetId
└── Value : string    (non-null, non-empty; format: vendor-scoped identifier)
```

Equality by `Value`. Used as the idempotency key for Bronze layer partition overwrite.

---

### Value Object: `ValidationResult`

```
ValidationResult
├── IsSuccess       : bool
├── PassCount       : int
├── FailCount       : int
└── Failures        : IReadOnlyList<ValidationFailure>
      └── ValidationFailure
            ├── RowIndex  : int
            ├── FieldName : string
            ├── Rule      : string    (e.g., "RequiredField", "NumericRange", "UnknownSchema")
            └── Detail    : string
```

Immutable. Created by domain validation service after scanning all `RawRecord` instances.
`IsSuccess` is `true` only when `FailCount == 0`.

---

### Value Object: `RawRecord`

```
RawRecord
├── RowIndex  : int
└── Fields    : IReadOnlyDictionary<string, string>   (vendor column name → raw string value)
```

Produced directly by the CSV parser. Carries no type conversions; all values are strings
at this stage. Immutable after creation.

---

### Value Object: `CanonicalRecord`

```
CanonicalRecord
├── SiteId           : string
├── Timestamp        : DateTimeOffset    (ISO 8601 UTC, normalised from vendor datetime)
├── IngestionTime    : DateTimeOffset    (UTC, set at processing time)
├── SourceDatasetId  : string
├── SchemaVersion    : string
└── Fields           : IReadOnlyDictionary<string, object>   (canonical name → typed value)
```

Produced by the transformation/enrichment step. Immutable. Written to Bronze layer as Parquet.

---

### Value Object: `ProcessingMetrics`

```
ProcessingMetrics
├── DatasetId            : string
├── RecordsProcessed     : int
├── ValidationPassCount  : int
├── ValidationFailCount  : int
├── ProcessingDurationMs : long
└── BronzePath           : Uri?    (null if write did not occur)
```

Emitted as OpenTelemetry custom metrics at end of execution.

---

## Application Layer

### Command: `ProcessDatasetCommand`

```
ProcessDatasetCommand : IRequest<ProcessDatasetResult>
├── DatasetId      : string
├── StoragePath    : string   (ADLS URI)
├── VendorId       : string
├── SchemaVersion  : string
└── CorrelationId  : string
```

Dispatched by the function trigger immediately after deserialising the Service Bus message.

---

### Result: `ProcessDatasetResult`

```
ProcessDatasetResult
├── Success          : bool
├── RecordCount      : int
├── BronzePath       : string?
├── ValidationSummary: ValidationResult
└── ErrorMessage     : string?
```

---

### Notification: `DatasetBronzeAvailableNotification`

```
DatasetBronzeAvailableNotification : INotification
├── DatasetId      : string
├── RecordCount    : int
├── BronzePath     : string   (ADLS URI of written partition)
├── SchemaVersion  : string
└── CorrelationId  : string
```

Published by `ProcessDatasetCommandHandler` after a successful Bronze write.
Handled by `DatasetBronzeAvailablePublisher` (Infrastructure) which sends to Service Bus.

---

## Infrastructure Layer

### Entity: `VendorSchemaMapping`

Configuration record loaded from the external schema registry (Azure App Configuration or
Blob-hosted JSON).

```
VendorSchemaMapping
├── VendorId          : string
├── SchemaVersion     : string
├── Delimiter         : char      (comma, semicolon, or tab — FR-006a)
├── Encoding          : string    (must be "UTF-8" — FR-006b)
├── ColumnMappings    : IReadOnlyList<ColumnMapping>
│     └── ColumnMapping
│           ├── VendorColumn    : string
│           ├── CanonicalField  : string
│           ├── DataType        : FieldDataType   (String | Integer | Decimal | DateTime)
│           └── UnitConversion  : UnitConversion?
│                 ├── FromUnit  : string
│                 └── ToUnit    : string
└── RequiredFields    : IReadOnlyList<string>    (canonical field names that must be non-null)
```

---

## Integration Events (Messaging Contracts)

### Inbound: `dataset.available`

Published by upstream producer on `dataset-events` Service Bus topic.

```
DatasetAvailableEvent
├── dataset_id      : string    (globally unique, idempotency key)
├── storage_path    : string    (ADLS URI: abfss://...)
├── vendor_id       : string
├── schema_version  : string
├── correlation_id  : string    (W3C traceparent)
└── published_at    : string    (ISO 8601 UTC)
```

---

### Outbound: `dataset.bronze.available`

Published by this function after successful Bronze write (FR-025).

```
DatasetBronzeAvailableEvent
├── dataset_id      : string
├── record_count    : int
├── bronze_path     : string    (ADLS URI of written Parquet partition)
├── schema_version  : string
├── correlation_id  : string    (propagated from inbound event)
└── published_at    : string    (ISO 8601 UTC)
```

Follows medallion naming convention: `dataset.{layer}.available` (FR-027).

---

## State Transitions

```
[Received]
    │  FR-002: extract dataset_id + storage_path
    ▼
[Retrieving]
    │  FR-004: ADLS read via DataLakeServiceClient
    │  FR-005: retry on transient failure → DLQ on max delivery
    ▼
[Parsing]
    │  FR-006: detect header, read delimiter from VendorSchemaMapping
    │  FR-006b: verify UTF-8 encoding → DLQ if invalid
    │  FR-007: convert fields to typed values
    ▼
[Validating]
    │  FR-009: timestamp required
    │  FR-010: numeric range checks
    │  FR-011: schema match
    │  FR-012: emit ValidationResult metrics
    │  [FAIL] → DLQ (FR-019: no partial Bronze write)
    ▼
[Transforming]
    │  FR-013: vendor column → canonical field mapping
    │  FR-014: datetime → ISO 8601 UTC
    │  FR-015: unit normalisation
    │  FR-016: enrichment (site_id, ingestion_time, etc.)
    ▼
[Writing]
    │  FR-017: Parquet → OneLake Bronze partition
    │  FR-018: atomic overwrite by dataset_id + ingestion_date
    ▼
[Completed]
    │  FR-025: publish dataset.bronze.available event
    │  FR-020–FR-023: emit OTel spans + metrics
    ▼
[Done]
```
