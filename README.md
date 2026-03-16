# Dataset Processing Function

.NET 10 Azure Isolated Worker Function that processes vendor CSV datasets from ADLS Gen2,
validates and transforms records into the platform canonical schema, writes Parquet to the
Fabric Lakehouse Bronze layer, and publishes `dataset.bronze.available` downstream.

## Architecture

Clean architecture with four layers:

| Project | Role |
|---------|------|
| `DatasetProcessingFunction` | Azure Functions host, DI wiring, OTel bootstrap |
| `DatasetProcessingFunction.Application` | CQRS commands/notifications, port interfaces |
| `DatasetProcessingFunction.Domain` | Value objects, aggregates, domain services |
| `DatasetProcessingFunction.Infrastructure` | ADLS, OneLake, Service Bus, Schema Registry |

## Quick Start (Local Dev)

See [specs/001-dataset-ingestion-pipeline/quickstart.md](specs/001-dataset-ingestion-pipeline/quickstart.md)
for full local dev setup including Azurite and Service Bus Emulator.

### Prerequisites

- .NET 10 SDK
- Azure Functions Core Tools v4
- Docker Desktop (for Azurite + Service Bus emulator)

### Run locally

```bash
cp src/DatasetProcessingFunction/local.settings.json.template \
   src/DatasetProcessingFunction/local.settings.json
# Edit local.settings.json with your emulator values
dotnet build DatasetProcessingFunction.slnx
cd src/DatasetProcessingFunction && func start
```

### Run tests

```bash
# Unit tests
dotnet test tests/DatasetProcessingFunction.UnitTests/

# Integration tests (mocked infrastructure, no Docker required)
dotnet test tests/DatasetProcessingFunction.IntegrationTests/

# All tests with coverage
dotnet test DatasetProcessingFunction.slnx \
  --collect:"XPlat Code Coverage" \
  --results-directory ./TestResults
```

## Deployment

Uses Azure Developer CLI (AZD) with Bicep IaC:

```bash
azd auth login
azd up --environment dev
```

GitHub Actions CI/CD runs on every push to `main` with OIDC Workload Identity Federation.
