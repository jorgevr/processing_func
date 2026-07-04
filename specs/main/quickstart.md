# Quickstart: Dataset Ingestion Pipeline (Local Development)

**Date**: 2026-03-24

---

## Prerequisites

| Tool | Version | Purpose |
| --- | --- | --- |
| .NET SDK | 10.x | Build and run the function |
| Docker Desktop | 4.x+ | Azurite + Service Bus Emulator |
| Azure Functions Core Tools | v4 | Local function host |
| PowerShell | 7+ | Dev scripts |

---

## 1. Start Local Infrastructure

```bash
# From the service root (processing-func/)
docker compose up -d
```

This starts:

- **Azurite** on ports 10000 (Blob), 10001 (Queue), 10002 (Table) — used for ADLS input, Bronze output, and schema registry
- **Service Bus Emulator** on port 5672 (AMQP) — used for the function trigger and outbound event publication
- **SQL Server 2022** on port 1433 — backing store for the Service Bus Emulator

Wait ~15 seconds for all containers to be healthy before proceeding.

---

## 2. Seed the Schema Registry

Upload a vendor schema mapping blob to Azurite so the function can resolve it at runtime.

```bash
# Using Azure CLI with Azurite connection string
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

---

## 3. Seed a Test CSV File

Upload a CSV fixture to the Azurite Blob store so the function can read it via the ADLS path.

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

The function will start and bind to the Service Bus Emulator topic `dataset-events` / subscription `dataset-processing`.

Verify startup by checking for:

```text
[Information] Host started
[Information] ProcessDatasetFunction: Listening to ServiceBus topic dataset-events, subscription dataset-processing
```

---

## 5. Publish a Test Event

Use the Service Bus Emulator's management endpoint or the Azure SDK to send a `dataset.available` message.

**Using a small .NET snippet or the integration test helper:**

```json
{
  "DatasetId": "vendor-abc-20260315-001",
  "StoragePath": "http://127.0.0.1:10000/devstoreaccount1/raw/datasets/vendor-abc/20260315/data.csv",
  "VendorId": "vendor-abc",
  "SchemaVersion": "v2",
  "CorrelationId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "PublishedAt": "2026-03-15T10:00:00Z"
}
```

Send as a Service Bus message with `Subject: dataset.available` to topic `dataset-events`.

---

## 6. Verify Output

**Bronze Parquet written:**

```bash
az storage blob list \
  --container-name bronze \
  --connection-string "UseDevelopmentStorage=true" \
  --output table
```

Expect a blob at: `vendor-abc-20260315-001/2026-03-15/data.parquet`

**Downstream event published:**

Check the `dataset-events` topic for a message with `Subject: dataset.bronze.available`.

---

## 7. Run Tests

```bash
# Unit tests only (no Docker required)
dotnet test tests/DatasetProcessingFunction.UnitTests/

# Integration tests (requires docker compose up)
dotnet test tests/DatasetProcessingFunction.IntegrationTests/

# All with coverage
dotnet test --collect:"XPlat Code Coverage" --results-directory ./coverage
```

---

## Environment Variables (local.settings.json)

| Setting | Local Value | Description |
| --- | --- | --- |
| `AzureWebJobsStorage` | `UseDevelopmentStorage=true` | Functions internal storage |
| `FUNCTIONS_WORKER_RUNTIME` | `dotnet-isolated` | Required for isolated worker |
| `ServiceBusConnection` | `Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;` | Service Bus Emulator (SAS string — identity auth not supported by emulator) |
| `SERVICEBUS_TOPIC_NAME` | `dataset-events` | Topic for inbound + outbound events |
| `SERVICEBUS_SUBSCRIPTION_NAME` | `dataset-processing` | Subscription for inbound events |
| `ADLS_ENDPOINT` | `http://127.0.0.1:10000/devstoreaccount1` | Azurite ADLS endpoint for input reads |
| `ONELAKE_ENDPOINT` | `http://127.0.0.1:10000/devstoreaccount1` | Azurite endpoint for Bronze writes |
| `SCHEMA_REGISTRY_BLOB_CONNECTION` | `UseDevelopmentStorage=true` | Azurite connection for schema blobs |
| `SCHEMA_REGISTRY_CONTAINER` | `schema-registry` | Container name for schema mapping blobs |
| `BRONZE_FILESYSTEM` | `bronze` | Container/filesystem for Bronze output |
| `OTEL_SERVICE_NAME` | `dataset-processing-func-local` | OTel service name (local dev) |

---

## Troubleshooting

| Symptom | Likely Cause | Fix |
| --- | --- | --- |
| Function fails to bind to Service Bus | Emulator not ready | Wait 10–15 s after `docker compose up` before `func start` |
| `No schema mapping found for vendor-abc:v2` | Schema blob not uploaded | Run step 2 (seed schema registry) |
| `UnsupportedEncodingException` | CSV fixture not UTF-8 | Re-save fixture as UTF-8 without BOM |
| `EmptyDatasetException` | Fixture file has header only | Use `vendor-abc-v2-valid.csv` (5 data rows) |
| Bronze blob not created | ADLS endpoint mismatch | Ensure `ADLS_ENDPOINT` and `ONELAKE_ENDPOINT` both point to Azurite |
