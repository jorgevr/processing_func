# Research: Dataset Ingestion Pipeline (Raw → Bronze)

**Date**: 2026-03-24 | **Plan**: [plan.md](plan.md)

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
        _                       => new DataField<string>(canonicalName),  // string = ref type, always nullable
    };

static ParquetSchema BuildSchema(VendorSchemaMapping mapping)
{
    var enrichmentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "site_id", "timestamp", "ingestion_time", "source_dataset_id", "schema_version" };

    var fields = new List<Field>
    {
        // Fixed enrichment columns — non-nullable, INT64 millis for datetime
        new DataField<string>("site_id"),
        new DateTimeDataField("timestamp",      DateTimeFormat.DateAndTime, isNullable: false),
        new DateTimeDataField("ingestion_time", DateTimeFormat.DateAndTime, isNullable: false),
        new DataField<string>("source_dataset_id"),
        new DataField<string>("schema_version"),
    };

    // Dynamic vendor columns — skip any that duplicate an enrichment field name
    foreach (var col in mapping.ColumnMappings
        .Where(c => !enrichmentNames.Contains(c.CanonicalField)))
    {
        fields.Add(CreateVendorField(col.CanonicalField, col.DataType));
    }

    return new ParquetSchema(fields);
}
```

**Column array extraction pattern:**

Columns must be written in exact schema order. `schema.DataFields` provides the flat ordered list. For vendor columns, extract typed arrays from `CanonicalRecord.Fields`:

```csharp
static Array BuildColumnArray(
    IReadOnlyList<CanonicalRecord> records, string fieldName, string dataType)
{
    int n = records.Count;
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

        "double"          => records.Select(r =>
            r.Fields.TryGetValue(fieldName, out var v) ? (double?)Convert.ToDouble(v) : null
            ).ToArray(),

        "int" or "integer" => records.Select(r =>
            r.Fields.TryGetValue(fieldName, out var v) ? (int?)Convert.ToInt32(v) : null
            ).ToArray(),

        "long"            => records.Select(r =>
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

- *`DataField<DateTimeOffset>` for datetime*: Rejected — produces INT96 (Impala legacy format); incompatible with modern Fabric/Spark readers.
- *`DataField<decimal>` for decimal*: Rejected — defaults to precision 29/scale 14; non-deterministic downstream behaviour.
- *Runtime reflection of `CanonicalRecord.Fields`*: Rejected — FR-017b explicitly forbids it.
- *Hardcoded schema per vendor*: Rejected — violates FR-013.

**Gotchas:**

| Issue | Detail |
| --- | --- |
| `DataField<DateTimeOffset>` = INT96 | Always use `DateTimeDataField(..., DateTimeFormat.DateAndTime)` and pass `.UtcDateTime` in the array |
| `DataField<decimal>` implicit precision | Always use `DecimalDataField(name, 18, 6, isNullable: true)` for explicit precision/scale |
| Column write order | Must exactly match `schema.DataFields` order; offset vendor columns by 5 (enrichment count) |
| Array element type must match field CLR type | `DataColumn(field, array)` throws if types don't match; `decimal?[]` for `DecimalDataField`, `DateTime?[]` for `DateTimeDataField` |
| `string` columns | Reference type — inherently nullable; no `isNullable` needed |
| `ParquetSchema` constructor | Accepts `IEnumerable<Field>` — pass a `List<Field>` directly |

---

## 2. Microsoft Service Bus Emulator (Docker)

**Decision:** Use `mcr.microsoft.com/azure-messaging/servicebus-emulator:latest` alongside Azurite in `docker-compose.yml`. Backing store is `mcr.microsoft.com/mssql/server:2022-latest`. The `local.settings.json` must use a **SAS connection string** with `UseDevelopmentEmulator=true` — the `fullyQualifiedNamespace` / Managed Identity pattern is not supported by the emulator.

**Rationale:** The emulator is the only local option that supports the full Service Bus SDK including `ServiceBusTrigger` in isolated worker functions. Azurite Storage Queues lack topic/subscription semantics and dead-letter support required by the spec.

**docker-compose.yml:**

```yaml
name: dataset-processing-local

services:
  azurite:
    image: mcr.microsoft.com/azure-storage/azurite:latest
    ports:
      - "10000:10000"   # Blob
      - "10001:10001"   # Queue
      - "10002:10002"   # Table
    command: azurite --blobHost 0.0.0.0 --queueHost 0.0.0.0 --tableHost 0.0.0.0

  emulator:
    container_name: servicebus-emulator
    image: mcr.microsoft.com/azure-messaging/servicebus-emulator:latest
    pull_policy: always
    volumes:
      - "${CONFIG_PATH}:/ServiceBus_Emulator/ConfigFiles/Config.json"
    ports:
      - "5672:5672"     # AMQP
      - "5300:5300"     # HTTP management / health-check
    environment:
      SQL_SERVER: mssql
      MSSQL_SA_PASSWORD: "${MSSQL_SA_PASSWORD}"
      ACCEPT_EULA: "${ACCEPT_EULA}"
    depends_on:
      - mssql
    networks:
      sb-emulator:
        aliases:
          - sb-emulator

  mssql:
    container_name: mssql
    image: mcr.microsoft.com/mssql/server:2022-latest
    networks:
      sb-emulator:
        aliases:
          - mssql
    environment:
      ACCEPT_EULA: "${ACCEPT_EULA}"
      MSSQL_SA_PASSWORD: "${MSSQL_SA_PASSWORD}"

networks:
  sb-emulator:
```

Accompanying `.env` (alongside `docker-compose.yml`):

```env
CONFIG_PATH=C:\\code\\anomalia-platform\\services\\processing-func\\emulator\\Config.json
ACCEPT_EULA=Y
MSSQL_SA_PASSWORD=YourStr0ng!Pass
```

**emulator/Config.json** — namespace name MUST be `sbemulatorns` (fixed, not configurable):

```json
{
  "UserConfig": {
    "Namespaces": [{
      "Name": "sbemulatorns",
      "Topics": [{
        "Name": "dataset-events",
        "Properties": {
          "DefaultMessageTimeToLive": "PT1H",
          "RequiresDuplicateDetection": false
        },
        "Subscriptions": [{
          "Name": "dataset-processing",
          "Properties": {
            "LockDuration": "PT1M",
            "MaxDeliveryCount": 3,
            "RequiresSession": false
          }
        }]
      }]
    }],
    "Logging": { "Type": "File" }
  }
}
```

**local.settings.json connection string** — use `ServiceBusConnection` (plain string), NOT `ServiceBusConnection__fullyQualifiedNamespace`:

```json
"ServiceBusConnection": "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;"
```

**Program.cs dual-mode ServiceBusClient** — the emulator does not support Entra ID / Managed Identity. `Program.cs` must detect the emulator connection string and bypass credential construction:

```csharp
var sbConnStr = builder.Configuration["ServiceBusConnection"];
ServiceBusClient sbClient;
if (!string.IsNullOrWhiteSpace(sbConnStr) &&
    sbConnStr.Contains("UseDevelopmentEmulator", StringComparison.OrdinalIgnoreCase))
{
    sbClient = new ServiceBusClient(sbConnStr);  // emulator: SAS connection string
}
else
{
    var ns = builder.Configuration["ServiceBusConnection:fullyQualifiedNamespace"]
        ?? throw new InvalidOperationException(
            "ServiceBusConnection__fullyQualifiedNamespace is required.");
    sbClient = new ServiceBusClient(ns, credential);  // production: Managed Identity
}
builder.Services.AddSingleton(sbClient);
```

**Alternatives considered:**

- *Shared dev Azure namespace*: Rejected — cloud dependency blocks offline development; shared namespace causes test interference.
- *Azurite Storage Queues*: Rejected — no topic/subscription model; dead-letter semantics absent; `ServiceBusTrigger` will not bind to a storage queue.

**Gotchas:**

| Issue | Detail |
| --- | --- |
| Namespace name is fixed | Must be `sbemulatorns` — any other name silently fails |
| No identity auth | `fullyQualifiedNamespace` + `DefaultAzureCredential` is rejected; use SAS with `UseDevelopmentEmulator=true` |
| SQL password policy | `MSSQL_SA_PASSWORD` must be ≥ 8 chars with mixed case, digit, and special character or SQL container silently fails to start |
| Config not hot-reloaded | Config.json changes require `docker compose restart emulator` |
| Windows `.env` path | `CONFIG_PATH` requires double backslashes on Windows |
| No data persistence | All messages lost on container restart — intentional for test isolation |
| Only 1 namespace | Emulator supports exactly one namespace (`sbemulatorns`) |

---

## 3. Polly v8 Retry for ADLS Transient Failures

**Decision:** Use `Microsoft.Extensions.Resilience` (Polly v8 integration) registered via `AddResiliencePipeline` in DI. Apply the pipeline inside `AdlsDatasetReader.ReadAsync`, handling `RequestFailedException` with transient HTTP status codes (429, 500, 503, 408) and `IOException` for transport-level failures. Treat 404/403/400/409 as permanent — do not retry. **Critically: disable the Azure SDK's own built-in retry (`MaxRetries = 0` on `DataLakeClientOptions`) to prevent multiplicative retries.**

**Rationale:** `Microsoft.Extensions.Resilience` is the recommended Polly v8 integration for .NET 10. Registering via DI allows the pipeline to be swapped with a no-op in unit tests. The spec requires 3 total attempts (= 2 retries after the initial call), 2 s base delay, 30 s max delay. The Azure SDK defaults to 5 retries internally — without disabling this, a Polly attempt with 2 SDK retries = 6 HTTP calls, and 3 Polly attempts = up to 18 actual requests against the spec's "3 maximum".

**Pattern:**

```csharp
// Program.cs — DataLakeServiceClient registration: disable SDK built-in retry
builder.Services.AddSingleton(sp =>
{
    var adlsEndpoint = builder.Configuration["ADLS_ENDPOINT"]
        ?? throw new InvalidOperationException("ADLS_ENDPOINT is required.");
    var options = new DataLakeClientOptions
    {
        Retry = { MaxRetries = 0 }   // Polly owns retry; SDK layer must not also retry
    };
    return new DataLakeServiceClient(new Uri(adlsEndpoint), credential, options);
});

// Program.cs — Polly pipeline registration
builder.Services.AddResiliencePipeline("adls-read", pipeline =>
    pipeline.AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts = 2,           // 2 retries = 3 total attempts
        Delay = TimeSpan.FromSeconds(2),
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,               // avoids thundering herd on shared ADLS
        MaxDelay = TimeSpan.FromSeconds(30),
        ShouldHandle = new PredicateBuilder()
            .Handle<IOException>()      // transport-level failures (connection reset, DNS)
            .Handle<RequestFailedException>(ex =>
                ex.Status is 429 or 500 or 503 or 408)
    }));

// AdlsDatasetReader
public AdlsDatasetReader(
    DataLakeServiceClient serviceClient,
    ResiliencePipelineProvider<string> pipelineProvider,
    ILogger<AdlsDatasetReader> logger)
{
    _pipeline = pipelineProvider.GetPipeline("adls-read");
    // ...
}

public async Task<Stream> ReadAsync(Uri storagePath, CancellationToken ct)
{
    return await _pipeline.ExecuteAsync(async token =>
    {
        var (fs, path) = ParseAdlsUri(storagePath);
        var fileClient = _serviceClient
            .GetFileSystemClient(fs).GetFileClient(path);
        var response = await fileClient.ReadAsync(token);
        var ms = new MemoryStream();
        await response.Value.Content.CopyToAsync(ms, token);
        ms.Position = 0;
        return ms;
    }, ct);
}
```

**Transient vs permanent classification:**

| Status | Class | Action |
| --- | --- | --- |
| 404 Not Found | Permanent | Do not retry; propagate → SB delivery count increments |
| 403 Forbidden | Permanent | Do not retry; likely auth misconfiguration |
| 400 Bad Request | Permanent | Do not retry; malformed URI |
| 409 Conflict | Permanent | Do not retry; path collision |
| 429 Too Many Requests | Transient | Retry with back-off |
| 500 Internal Server Error | Transient | Retry |
| 503 Service Unavailable | Transient | Retry |
| 408 Request Timeout | Transient | Retry |
| `IOException` (network) | Transient | Retry |

**Alternatives considered:**

- *Inline Polly v7 style*: Rejected — deprecated in .NET 10; does not integrate with `IServiceProvider` or `ILogger`.
- *Azure SDK built-in retry alone*: Rejected — SDK cannot distinguish 404 (permanent) from transient errors at the domain level; Polly provides this classification.
- *`Microsoft.Extensions.Http.Resilience`*: Rejected — scoped to `HttpClient` pipelines only; has no hook for `DataLakeFileClient`.

**Gotchas:**

| Issue | Detail |
| --- | --- |
| Azure SDK has its own retry | Default `MaxRetries = 5`. Without disabling it, Polly × SDK = multiplicative retries far exceeding the spec's 3-attempt limit. Set `DataLakeClientOptions.Retry.MaxRetries = 0`. |
| `MaxRetryAttempts = 2` | Means 2 retries after the initial attempt = 3 total. Spec says "3 attempts" — set `MaxRetryAttempts = 2`, not 3. |
| `CancellationToken` | Passed to `ExecuteAsync` — cancellation during backoff delay aborts immediately without consuming an attempt. `OperationCanceledException` is not matched by `ShouldHandle` and propagates immediately. |
| After exhaustion | Polly v8 re-throws the last exception as-is without wrapping — Service Bus delivery count increments correctly. |
| NuGet package | `Microsoft.Extensions.Resilience` (not `Polly` directly, not `Microsoft.Extensions.Http.Resilience`). |

---

## Summary of Decisions

| Topic | Decision |
| --- | --- |
| Parquet dynamic schema | Build from `VendorSchemaMapping.ColumnMappings`; nullable vendor fields; fixed non-nullable enrichment columns; vendor fields that duplicate enrichment names are skipped |
| Local Service Bus | `mcr.microsoft.com/azure-messaging/servicebus-emulator` + SQL Server 2022 in docker-compose; SAS connection string with `UseDevelopmentEmulator=true`; namespace fixed as `sbemulatorns` |
| ADLS retry | `Microsoft.Extensions.Resilience`; `MaxRetryAttempts = 2` (3 total); exponential 2 s base, 30 s max; transient 429/500/503/0 only; 404 is permanent |
