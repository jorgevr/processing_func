# Research: Dataset Ingestion Pipeline (Raw → Bronze)

**Date**: 2026-04-16 | **Plan**: [plan.md](plan.md)

---

## 1. Parquet.Net 4.23.x Dynamic Schema from Configuration

**Decision:** Build `ParquetSchema` at write time by iterating `VendorSchemaMapping.ColumnMappings` and mapping each `data_type` string to a `Field` instance. Use `DateTimeDataField` (not `DataField<DateTimeOffset>`) for all datetime columns to produce INT64 millis instead of legacy INT96. Use `DecimalDataField` with explicit precision/scale for all decimal columns. Use the non-generic `DataField(name, Type, isNullable)` constructor for runtime type dispatch.

**Rationale:** Parquet.Net 4.x schema must be fully specified before any row group is opened. The non-generic constructor enables fully dynamic schema construction. `DateTimeDataField` and `DecimalDataField` are required for downstream compatibility with Fabric/Spark/Power BI — the generic `DataField<DateTimeOffset>` and `DataField<decimal>` produce legacy INT96 and imprecise decimal formats respectively.

**Schema construction pattern:**

```csharp
static Field CreateVendorField(string canonicalName, string dataType) =>
    dataType.ToLowerInvariant() switch
    {
        "datetime"              => new DateTimeDataField(canonicalName,
                                       DateTimeFormat.DateAndTime, isNullable: true),
        "decimal"               => new DecimalDataField(canonicalName,
                                       precision: 18, scale: 6, isNullable: true),
        "double"                => new DataField(canonicalName, typeof(double?),  isNullable: true),
        "float"                 => new DataField(canonicalName, typeof(float?),   isNullable: true),
        "int" or "integer"      => new DataField(canonicalName, typeof(int?),     isNullable: true),
        "long"                  => new DataField(canonicalName, typeof(long?),    isNullable: true),
        "bool" or "boolean"     => new DataField(canonicalName, typeof(bool?),    isNullable: true),
        _                       => new DataField<string>(canonicalName),
    };

static ParquetSchema BuildSchema(VendorSchemaMapping mapping)
{
    var enrichmentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "site_id", "timestamp", "ingestion_time", "source_dataset_id", "schema_version" };

    var fields = new List<Field>
    {
        new DataField<string>("site_id"),
        new DateTimeDataField("timestamp",      DateTimeFormat.DateAndTime, isNullable: false),
        new DateTimeDataField("ingestion_time", DateTimeFormat.DateAndTime, isNullable: false),
        new DataField<string>("source_dataset_id"),
        new DataField<string>("schema_version"),
    };

    foreach (var col in mapping.ColumnMappings
        .Where(c => !enrichmentNames.Contains(c.CanonicalField)))
    {
        fields.Add(CreateVendorField(col.CanonicalField, col.DataType));
    }

    return new ParquetSchema(fields);
}
```

**Column array extraction pattern:**

```csharp
static Array BuildColumnArray(
    IReadOnlyList<CanonicalRecord> records, string fieldName, string dataType)
{
    return dataType.ToLowerInvariant() switch
    {
        "string" => records.Select(r =>
            r.Fields.TryGetValue(fieldName, out var v) ? v as string ?? v?.ToString() : null
            ).ToArray(),

        "decimal" => records.Select(r =>
            r.Fields.TryGetValue(fieldName, out var v) && v is decimal d ? (decimal?)d : null
            ).ToArray(),

        "datetime" => records.Select(r =>
            r.Fields.TryGetValue(fieldName, out var v)
                ? v is DateTimeOffset dto ? (DateTime?)dto.UtcDateTime
                : v is DateTime dt ? (DateTime?)dt : null
                : null).ToArray(),

        "double"           => records.Select(r =>
            r.Fields.TryGetValue(fieldName, out var v) ? (double?)Convert.ToDouble(v) : null
            ).ToArray(),

        "int" or "integer" => records.Select(r =>
            r.Fields.TryGetValue(fieldName, out var v) ? (int?)Convert.ToInt32(v) : null
            ).ToArray(),

        "long"             => records.Select(r =>
            r.Fields.TryGetValue(fieldName, out var v) ? (long?)Convert.ToInt64(v) : null
            ).ToArray(),

        "bool" or "boolean" => records.Select(r =>
            r.Fields.TryGetValue(fieldName, out var v) && v is bool b ? (bool?)b : null
            ).ToArray(),

        _ => records.Select(r =>
            r.Fields.TryGetValue(fieldName, out var v) ? v?.ToString() : null
            ).ToArray(),
    };
}
```

**Alternatives considered:**

- *`DataField<DateTimeOffset>` for datetime*: Rejected — produces INT96 (Impala legacy); incompatible with modern Fabric/Spark readers.
- *`DataField<decimal>` for decimal*: Rejected — defaults to precision 29/scale 14; non-deterministic downstream behaviour.
- *Runtime reflection of `CanonicalRecord.Fields`*: Rejected — FR-017b explicitly forbids it.

**Gotchas:**

| Issue | Detail |
| --- | --- |
| `DataField<DateTimeOffset>` = INT96 | Always use `DateTimeDataField(..., DateTimeFormat.DateAndTime)` and pass `.UtcDateTime` in the array |
| `DataField<decimal>` implicit precision | Always use `DecimalDataField(name, 18, 6, isNullable: true)` |
| Column write order | Must exactly match `schema.DataFields` order; enrichment columns first (5), then vendor columns |
| Array element type must match field CLR type | `DataColumn(field, array)` throws on type mismatch |
| `string` columns | Reference type — inherently nullable |

---

## 2. Microsoft Service Bus Emulator — Queue-Based Configuration

**Decision:** Use `mcr.microsoft.com/azure-messaging/servicebus-emulator:latest` alongside Azurite in `docker-compose.yml`. The emulator `Config.json` defines two **queues** (`raw-energy-events`, `dataset-bronze-available`) — no topics or subscriptions. The spec mandates Azure Service Bus Basic tier which supports queues only.

**Rationale:** The Basic tier does not offer topics or subscriptions. All trigger and publisher code must use queue semantics. The emulator supports both queues and topics; the `Config.json` governs which entities are provisioned.

**`emulator/Config.json`** — namespace name MUST be `sbemulatorns`:

```json
{
  "UserConfig": {
    "Namespaces": [{
      "Name": "sbemulatorns",
      "Queues": [
        {
          "Name": "raw-energy-events",
          "Properties": {
            "DefaultMessageTimeToLive": "PT1H",
            "LockDuration": "PT1M",
            "MaxDeliveryCount": 3,
            "RequiresDuplicateDetection": false,
            "RequiresSession": false
          }
        },
        {
          "Name": "dataset-bronze-available",
          "Properties": {
            "DefaultMessageTimeToLive": "PT1H",
            "LockDuration": "PT1M",
            "MaxDeliveryCount": 3,
            "RequiresDuplicateDetection": false,
            "RequiresSession": false
          }
        }
      ]
    }],
    "Logging": { "Type": "File" }
  }
}
```

**`[ServiceBusTrigger]` attribute** (queue-based):

```csharp
[Function("ProcessDataset")]
public async Task RunAsync(
    [ServiceBusTrigger("%SERVICEBUS_QUEUE_NAME%",
        Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
    ServiceBusMessageActions messageActions,
    CancellationToken cancellationToken)
```

**`local.settings.json` connection string:**

```json
"ServiceBusConnection": "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;",
"SERVICEBUS_QUEUE_NAME": "raw-energy-events",
"SERVICEBUS_BRONZE_QUEUE_NAME": "dataset-bronze-available"
```

**Program.cs dual-mode `ServiceBusClient`:**

```csharp
var sbConnStr = builder.Configuration["ServiceBusConnection"];
ServiceBusClient sbClient;
if (!string.IsNullOrWhiteSpace(sbConnStr) &&
    sbConnStr.Contains("UseDevelopmentEmulator", StringComparison.OrdinalIgnoreCase))
{
    sbClient = new ServiceBusClient(sbConnStr);
}
else
{
    var ns = builder.Configuration["ServiceBusConnection:fullyQualifiedNamespace"]
        ?? throw new InvalidOperationException(
            "ServiceBusConnection__fullyQualifiedNamespace is required.");
    sbClient = new ServiceBusClient(ns, credential);
}
builder.Services.AddSingleton(sbClient);
```

**Gotchas:**

| Issue | Detail |
| --- | --- |
| Namespace name is fixed | Must be `sbemulatorns` — any other name silently fails |
| No identity auth in emulator | Use SAS with `UseDevelopmentEmulator=true`; Managed Identity rejected |
| Basic tier = queues only | Do not add `Topics` to `Config.json`; emulator accepts it but prod Service Bus will not |
| SQL password policy | `MSSQL_SA_PASSWORD` must be ≥ 8 chars with mixed case, digit, and special char |

---

## 3. Polly v8 Retry for ADLS Transient Failures (FR-005)

**Decision:** Use `Microsoft.Extensions.Resilience` (Polly v8) registered via `AddResiliencePipeline`. Apply inside `AdlsDatasetReader.ReadAsync`. Classify 404/403/400/409 as permanent (no retry). **Disable Azure SDK built-in retry (`MaxRetries = 0`) to prevent multiplicative attempts.**

**Rationale:** FR-005 requires 3 total attempts (= 2 retries), 2 s base, 30 s max. The Azure SDK defaults to 5 retries — without disabling it, Polly × SDK = up to 18 actual HTTP calls against the spec's limit.

**Pattern:**

```csharp
// Program.cs — disable SDK built-in retry
builder.Services.AddSingleton(sp =>
{
    var options = new DataLakeClientOptions { Retry = { MaxRetries = 0 } };
    return new DataLakeServiceClient(new Uri(adlsEndpoint), credential, options);
});

// Program.cs — Polly pipeline
builder.Services.AddResiliencePipeline("adls-read", pipeline =>
    pipeline.AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts = 2,
        Delay = TimeSpan.FromSeconds(2),
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        MaxDelay = TimeSpan.FromSeconds(30),
        ShouldHandle = new PredicateBuilder()
            .Handle<IOException>()
            .Handle<RequestFailedException>(ex =>
                ex.Status is 429 or 500 or 503 or 408)
    }));
```

**Transient vs permanent:**

| Status | Class | Action |
| --- | --- | --- |
| 404 Not Found | Permanent | Do not retry; propagate → SB delivery count increments |
| 403 Forbidden | Permanent | Do not retry; auth misconfiguration |
| 429 Too Many Requests | Transient | Retry with back-off |
| 500/503/408 | Transient | Retry |
| `IOException` | Transient | Retry |

**Gotcha:** `MaxRetryAttempts = 2` = 2 retries after initial = 3 total. Spec says "3 attempts" — set `MaxRetryAttempts = 2`.

---

## 4. Polly v8 Retry for Schema Registry Reads (FR-013a)

**Decision:** Apply the same Polly pattern to `BlobSchemaRegistry.GetMappingAsync`. Register a separate `"schema-registry-read"` pipeline. A 404 (blob not found = mapping absent) is **non-retriable** and must throw `UnknownSchemaException` immediately. Transient 429/500/503/408 and `IOException` are retriable.

**Rationale:** FR-013a mandates symmetry with the ADLS retry policy. A transient schema registry outage would prematurely dead-letter events if not retried. A missing mapping (404) is a permanent condition — the mapping does not exist — and must fail fast with `UnknownSchemaException`.

**Pattern:**

```csharp
// Program.cs
builder.Services.AddResiliencePipeline("schema-registry-read", pipeline =>
    pipeline.AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts = 2,
        Delay = TimeSpan.FromSeconds(2),
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        MaxDelay = TimeSpan.FromSeconds(30),
        ShouldHandle = new PredicateBuilder()
            .Handle<IOException>()
            .Handle<RequestFailedException>(ex =>
                ex.Status is 429 or 500 or 503 or 408)
        // 404 is NOT in ShouldHandle — it propagates immediately
    }));

// BlobSchemaRegistry
public async Task<VendorSchemaMapping?> GetMappingAsync(
    string vendorId, string schemaVersion, CancellationToken ct)
{
    return await _pipeline.ExecuteAsync(async token =>
    {
        var blobName = $"{vendorId}-{schemaVersion}.json";
        var blobClient = _containerClient.GetBlobClient(blobName);
        try
        {
            var response = await blobClient.DownloadContentAsync(token);
            return JsonSerializer.Deserialize<VendorSchemaMapping>(
                response.Value.Content.ToString());
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Non-retriable: mapping does not exist
            throw new UnknownSchemaException(vendorId, schemaVersion);
        }
    }, ct);
}
```

**Key distinction from ADLS retry:** 404 on ADLS = file referenced in event is missing (propagate for SB retry budget). 404 on schema registry = mapping was never registered (non-retriable; dead-letter immediately with `UnknownSchema`).

---

## 5. `site_id` Derivation from Canonical Field Mapping (FR-016)

**Decision:** `site_id` on each `CanonicalRecord` is populated from the CSV column whose `VendorSchemaMapping.ColumnMappings` entry has `canonical_field: "site_id"`. The `SchemaTransformer` writes this value into `CanonicalRecord.Fields["site_id"]`; the `RecordEnricher` then promotes it to the typed `CanonicalRecord.SiteId` property. A vendor schema with no `canonical_field: "site_id"` mapping is treated as unrecognised (`UnknownSchemaException`), enforced in `BlobSchemaRegistry` post-deserialization.

**Rationale:** `site_id` is a vendor-supplied value that varies per dataset; it cannot be derived mechanically from `dataset_id` without a vendor-specific parsing rule. The canonical field mapping pattern (FR-013) already handles the vendor→canonical translation; using the same mechanism for `site_id` keeps the system uniform and avoids a special-case derivation.

**Validation in `BlobSchemaRegistry`** (FR-016 + FR-016a — both checked together):

```csharp
var mapping = JsonSerializer.Deserialize<VendorSchemaMapping>(json);

if (!mapping.ColumnMappings.Any(c =>
    string.Equals(c.CanonicalField, "site_id", StringComparison.OrdinalIgnoreCase)))
{
    throw new UnknownSchemaException(vendorId, schemaVersion);
}

if (!mapping.ColumnMappings.Any(c =>
    string.Equals(c.CanonicalField, "timestamp", StringComparison.OrdinalIgnoreCase)))
{
    throw new UnknownSchemaException(vendorId, schemaVersion);
}

return mapping;
```

A missing `timestamp` mapping is a schema-level structural defect — not a per-record data-quality issue. Catching it here produces a single `UnknownSchema` DLQ entry with the correct `vendor_id`/`schema_version` context, rather than allowing every record to fail FR-009 as `ValidationFailed`.

---

## Summary of Decisions

| Topic | Decision |
| --- | --- |
| Parquet dynamic schema | Build from `VendorSchemaMapping.ColumnMappings`; nullable vendor fields; fixed non-nullable enrichment columns |
| Service Bus (local) | `mcr.microsoft.com/azure-messaging/servicebus-emulator` + queues only (Basic tier); `sbemulatorns` namespace; SAS connection string |
| ADLS retry | `Microsoft.Extensions.Resilience`; `MaxRetryAttempts = 2` (3 total); 2 s base, 30 s max; 404/403 permanent |
| Schema registry retry | Same Polly pattern; 404 = non-retriable `UnknownSchemaException`; transient faults retriable |
| `site_id` source | CSV column with `canonical_field: "site_id"` in `VendorSchemaMapping`; absent = `UnknownSchemaException` |
| `timestamp` mapping requirement | Schema-level check in `BlobSchemaRegistry`; absent = `UnknownSchemaException` (same as `site_id`; FR-016a) |
| `correlation_id` fallback | Optional field on `DatasetAvailableEvent`; if absent, generate `Guid.NewGuid().ToString()` at trigger boundary and log a warning; never dead-letter for absent `correlation_id` |
