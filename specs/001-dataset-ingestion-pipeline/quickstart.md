# Quickstart: Dataset Ingestion Pipeline

**Branch**: `001-dataset-ingestion-pipeline`

Get the function running locally end-to-end in under 10 minutes.

---

## Prerequisites

| Tool | Version | Install |
|------|---------|---------|
| .NET SDK | 10.x | `winget install Microsoft.DotNet.SDK.10` |
| Azure Functions Core Tools | v4.x | `npm i -g azure-functions-core-tools@4` |
| Docker Desktop | latest | https://www.docker.com/products/docker-desktop |
| Azure CLI | latest | `winget install Microsoft.AzureCLI` |
| `az login` authenticated | — | `az login` |

---

## 1. Start Local Infrastructure (Docker)

```bash
# Azurite (ADLS Gen2 emulator)
docker run -d --name azurite -p 10000:10000 -p 10001:10001 -p 10002:10002 \
  mcr.microsoft.com/azure-storage/azurite

# Azure Service Bus emulator
docker run -d --name servicebus-emulator -p 5672:5672 \
  mcr.microsoft.com/azure-messaging/servicebus-emulator:latest
```

---

## 2. Configure Local Settings

Copy the template and fill in the local emulator values:

```bash
cp src/DatasetProcessingFunction/local.settings.json.template \
   src/DatasetProcessingFunction/local.settings.json
```

`local.settings.json` (emulator values pre-filled in template):

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "ServiceBusConnection__fullyQualifiedNamespace": "localhost",
    "ADLS_ENDPOINT": "http://127.0.0.1:10000/devstoreaccount1",
    "ONELAKE_ENDPOINT": "http://127.0.0.1:10000/devstoreaccount1",
    "SCHEMA_REGISTRY_BLOB_CONNECTION": "UseDevelopmentStorage=true",
    "OTEL_SERVICE_NAME": "dataset-processing-func-local",
    "APPLICATIONINSIGHTS_CONNECTION_STRING": ""
  }
}
```

> `APPLICATIONINSIGHTS_CONNECTION_STRING` can be left empty locally — telemetry will be
> printed to console via the OpenTelemetry console exporter (dev-only, see Program.cs).

---

## 3. Seed a Test Dataset in Azurite

```bash
# Create input container
az storage container create --name raw --connection-string \
  "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tiqIRw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;"

# Upload a sample CSV fixture
az storage blob upload \
  --container-name raw \
  --name datasets/vendor-abc/20260315/data.csv \
  --file tests/DatasetProcessingFunction.IntegrationTests/fixtures/vendor-abc-v2-valid.csv \
  --connection-string "DefaultEndpointsProtocol=http;..."
```

---

## 4. Build and Run

```bash
dotnet build DatasetProcessingFunction.sln --configuration Debug

cd src/DatasetProcessingFunction
func start
```

Expected output:
```
[2026-03-15 10:30:00] Host started
[2026-03-15 10:30:00] Functions:
[2026-03-15 10:30:00]   ProcessDatasetFunction: serviceBusTrigger
```

---

## 5. Trigger Processing

Publish a `dataset.available` message to the local Service Bus emulator:

```bash
dotnet run --project tools/LocalEventPublisher -- \
  --dataset-id "vendor-abc-20260315-001" \
  --storage-path "http://127.0.0.1:10000/devstoreaccount1/raw/datasets/vendor-abc/20260315/data.csv" \
  --vendor-id "vendor-abc" \
  --schema-version "v2"
```

Expected function log output:
```
Processing dataset: vendor-abc-20260315-001
CSV parsed: 100 records
Validation: 100 passed, 0 failed
Transformation complete
Bronze write: http://127.0.0.1:10000/devstoreaccount1/bronze/vendor-abc-20260315-001/2026-03-15/data.parquet
Published: dataset.bronze.available
```

---

## 6. Run Tests

```bash
# Unit tests (no Docker required)
dotnet test tests/DatasetProcessingFunction.UnitTests/ --collect:"XPlat Code Coverage"

# Integration tests (requires Docker containers from step 1)
dotnet test tests/DatasetProcessingFunction.IntegrationTests/
```

Coverage report:
```bash
reportgenerator -reports:"**/coverage.cobertura.xml" -targetdir:"coveragereport" -reporttypes:Html
```

---

## 7. Verify Telemetry Locally

With `APPLICATIONINSIGHTS_CONNECTION_STRING` empty, OTel spans are emitted to the console.
Look for structured output like:

```
Activity.TraceId:    4bf92f3577b34da6a3ce929d0e0e4736
Activity.SpanId:     00f067aa0ba902b7
Activity.DisplayName: dataset.csv.parse
Activity.Duration:   00:00:00.0342891
```

---

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| `ServiceBusException: The messaging entity could not be found` | Service Bus emulator not running — re-run step 1 |
| `RequestFailedException: BlobNotFound` | CSV fixture not uploaded — re-run step 3 |
| `System.InvalidOperationException: No schema mapping found` | Seed the schema registry blob — see `tests/fixtures/schema-registry/vendor-abc-v2.json` |
| OTel traces missing from App Insights | Set `APPLICATIONINSIGHTS_CONNECTION_STRING` with a real connection string |
