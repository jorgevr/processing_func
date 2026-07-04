# Quickstart: Dataset Ingestion Pipeline (Local Development)

**Date**: 2026-04-16

---

## Prerequisites

| Tool | Version | Purpose |
| --- | --- | --- |
| .NET SDK | 10.x | Build and run the function |
| Docker Desktop | 4.x+ | Azurite + Service Bus Emulator |
| Azure Functions Core Tools | v4 | Local function host |
| Azure CLI | 2.x+ | Seed storage blobs |

---

## 1. Start Local Infrastructure

```bash
# From the service root (processing-func/)
docker compose up -d
```

This starts:

- **Azurite** on ports 10000 (Blob), 10001 (Queue), 10002 (Table) — ADLS input, Bronze output, schema registry
- **Service Bus Emulator** on port 5672 (AMQP) — queues `raw-energy-events` and `dataset-bronze-available`
- **SQL Server 2022** — backing store for the Service Bus Emulator

Wait ~15 seconds for all containers to be healthy before proceeding.

---

## 2. Seed the Schema Registry

Upload a vendor schema mapping blob to Azurite.

```bash
az storage container create \
  --name schema-registry \
  --connection-string "UseDevelopmentStorage=true"

az storage blob upload \
  --container-name schema-registry \
  --name "vendor-abc-v2.json" \
  --file "tests/DatasetProcessingFunction.IntegrationTests/fixtures/vendor-abc-v2-schema.json" \
  --connection-string "UseDevelopmentStorage=true"
```

Schema file format: see [contracts/vendor-schema-mapping.json](contracts/vendor-schema-mapping.json).
The schema **must** include a `ColumnMapping` entry with `"CanonicalField": "site_id"`.

---

## 3. Seed a Test CSV File

```bash
az storage container create \
  --name raw \
  --connection-string "UseDevelopmentStorage=true"

az storage blob upload \
  --container-name raw \
  --name "datasets/vendor-abc/20260315/data.csv" \
  --file "tests/DatasetProcessingFunction.IntegrationTests/fixtures/vendor-abc-v2-valid.csv" \
  --connection-string "UseDevelopmentStorage=true"
```

---

## 4. Run the Function Host

```bash
cd src/DatasetProcessingFunction
func start
```

Verify startup:

```text
[Information] Host started
[Information] ProcessDatasetFunction: Listening to ServiceBus queue raw-energy-events
```

---

## 5. Publish a Test Event

Send a `dataset.available` message to the `raw-energy-events` queue using the integration test helper or:

```json
{
  "DatasetId": "vendor-abc-20260315-001",
  "StoragePath": "http://127.0.0.1:10000/devstoreaccount1/raw/datasets/vendor-abc/20260315/data.csv",
  "VendorId": "vendor-abc",
  "SchemaVersion": "v2",
  "CorrelationId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "PublishedAt": "2026-04-16T10:00:00Z"
}
```

---

## 6. Verify Output

**Bronze Parquet written:**

```bash
az storage blob list \
  --container-name bronze \
  --connection-string "UseDevelopmentStorage=true" \
  --output table
```

Expect a blob at: `vendor-abc-20260315-001/2026-04-16/data.parquet`

**Downstream event published:**

Check the `dataset-bronze-available` queue for a message with the expected snake_case payload.

---

## 7. Run Tests

```bash
# Unit tests only (no Docker required)
dotnet test tests/DatasetProcessingFunction.UnitTests/

# Integration tests (requires docker compose up)
dotnet test tests/DatasetProcessingFunction.IntegrationTests/

# All with coverage report
dotnet test --collect:"XPlat Code Coverage" --results-directory ./coverage
```

---

## Environment Variables (`local.settings.json`)

| Setting | Local Value | Description |
| --- | --- | --- |
| `AzureWebJobsStorage` | `UseDevelopmentStorage=true` | Functions internal storage |
| `FUNCTIONS_WORKER_RUNTIME` | `dotnet-isolated` | Required for isolated worker |
| `ServiceBusConnection` | `Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;` | Service Bus Emulator (SAS — identity auth not supported) |
| `SERVICEBUS_QUEUE_NAME` | `raw-energy-events` | Inbound queue for `dataset.available` events |
| `SERVICEBUS_BRONZE_QUEUE_NAME` | `dataset-bronze-available` | Outbound queue for `dataset.bronze.available` events |
| `ADLS_ENDPOINT` | `http://127.0.0.1:10000/devstoreaccount1` | Azurite endpoint for CSV input reads |
| `ONELAKE_ENDPOINT` | `http://127.0.0.1:10000/devstoreaccount1` | Azurite endpoint for Bronze Parquet writes |
| `SCHEMA_REGISTRY_BLOB_CONNECTION` | `UseDevelopmentStorage=true` | Azurite connection for schema mapping blobs |
| `SCHEMA_REGISTRY_CONTAINER` | `schema-registry` | Container name for vendor schema mapping blobs |
| `BRONZE_FILESYSTEM` | `bronze` | Container/filesystem for Bronze Parquet output |
| `OTEL_SERVICE_NAME` | `dataset-processing-func-local` | OTel service name (stable across local restarts) |

---

## Troubleshooting

| Symptom | Likely Cause | Fix |
| --- | --- | --- |
| Function fails to bind to Service Bus | Emulator not ready | Wait 15 s after `docker compose up` before `func start` |
| `UnknownSchemaException: vendor-abc:v2` | Schema blob not uploaded or missing `site_id` mapping | Run step 2; verify schema JSON contains `"CanonicalField": "site_id"` |
| `UnsupportedEncodingException` | CSV fixture not UTF-8 | Re-save fixture as UTF-8 without BOM |
| `EmptyDatasetException` | Fixture has header only | Use `vendor-abc-v2-valid.csv` (has data rows) |
| Bronze blob not created | ADLS endpoint mismatch | Ensure `ADLS_ENDPOINT` and `ONELAKE_ENDPOINT` both point to Azurite |
| Unit conversion not applied | `UnitConversion` null or unrecognised key | Verify schema JSON `UnitConversion` value is one of: `kw_to_w`, `mw_to_w`, `kwh_to_wh`, `mwh_to_wh` |
