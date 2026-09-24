# AGENTS.md — processing-func

## 1. Purpose

Azure Functions (.NET 10 isolated worker) that consumes `solar.pvdaq.dataset.available` CloudEvents off Service Bus queue `raw-energy-events`, reads the source CSV from ADLS Gen2, validates/transforms it into the canonical schema, and writes Parquet to the `bronze` filesystem.
Publishes `dataset.bronze.available` to a downstream Service Bus queue on success; unprocessable messages are dead-lettered on the trigger queue.

## 2. Structure

- `src/DatasetProcessingFunction/` — Functions host: `Program.cs` DI wiring + OTel bootstrap, `ProcessDatasetFunction` (Service Bus trigger), `WarmupFunction`.
- `src/DatasetProcessingFunction.Application/` — MediatR commands/notifications, port interfaces (`IDatasetReader`, `IBronzeWriter`, `ISchemaRegistry`, `IEventPublisher`).
- `src/DatasetProcessingFunction.Domain/` — value objects, aggregates, domain services (CSV parsing, schema transform, validation, enrichment); no Azure SDK dependency.
- `src/DatasetProcessingFunction.Infrastructure/` — ADLS/OneLake, Service Bus, Schema Registry adapters implementing the Application ports.
- `tests/DatasetProcessingFunction.UnitTests/` — in-memory fakes, no Azure dependency.
- `tests/DatasetProcessingFunction.IntegrationTests/` — mocked infrastructure, no Docker needed.
- `infrastructure/` — Bicep modules + `azure.yaml` (AZD).
- `schemas/` — vendor schema registry JSON (shared contract — see §5).
- `scripts/` — local dev helpers (`seed-azurite.js`, `send-test-message`).

## 3. Commands

Verified working from the repo root on 2026-09-24:

| Purpose | Command |
| --- | --- |
| Setup (restore) | `dotnet restore DatasetProcessingFunction.slnx` |
| Build | `dotnet build DatasetProcessingFunction.slnx` |
| Lint/format check | `dotnet format DatasetProcessingFunction.slnx --verify-no-changes` |
| Unit tests | `dotnet test tests/DatasetProcessingFunction.UnitTests/` |
| Integration tests | `dotnet test tests/DatasetProcessingFunction.IntegrationTests/` (mocked infra, no Docker) |

**Needs the Docker emulator stack** (root `docker-compose.yml`: Azurite + Service Bus emulator) — don't start it, just note the dependency:
- Run locally: copy `local.settings.json.template` → `local.settings.json` with emulator values, then `cd src/DatasetProcessingFunction && func start`.

`dotnet format DatasetProcessingFunction.slnx --verify-no-changes` currently fails on pre-existing indentation issues (`WHITESPACE`) in 3 files — `OneLakeBronzeWriter.cs`, `ProcessDatasetFunction.cs`, `DataQualityValidatorTests.cs` — unrelated to this task. Don't rely on it until those are fixed.

## 4. Conventions

- **Layering**: Domain has no Azure SDK references; Infrastructure implements the ports declared in Application. `Program.cs`/Functions only wire DI and glue triggers to MediatR — business logic lives in command handlers, not the Function class.
- **DI**: expensive clients (`ServiceBusClient`, `DataLakeServiceClient`, `BlobServiceClient`) are Singletons in `Program.cs`. Dual-mode auth: connection-string/SAS for the local emulator (`UseDevelopmentEmulator`/`UseDevelopmentStorage`), otherwise `DefaultAzureCredential`.
- **Error handling**: `ProcessDatasetFunction` catches specific domain exceptions (`EmptyDatasetException`, `UnsupportedEncodingException`, `DatasetValidationException`, `UnknownSchemaException`, 404 `RequestFailedException`) and dead-letters with a distinct reason code + JSON detail. Any other exception is rethrown so Service Bus retries delivery — never swallow an unrecognized exception.
- **Retries**: Azure SDK retry is disabled (`DataLakeClientOptions.Retry.MaxRetries = 0`); a Polly v8 `ResiliencePipeline` per dependency (`adls-read`, `schema-registry-read`) owns retry (2 attempts, exponential backoff + jitter). 404 (unknown schema) is excluded — it's not transient.
- **Logging/OTel**: exporter-only OpenTelemetry path (`AddOpenTelemetry().WithTracing/WithMetrics().UseFunctionsWorkerDefaults()`) — never add the Azure Monitor AspNetCore distro (duplicate spans). Azure Monitor exporter wires only when `APPLICATIONINSIGHTS_CONNECTION_STRING` is set; the default App Insights `Warning+` `ILogger` filter rule is removed so all log levels flow through.
- **Idempotency**: Service Bus binding uses `autoComplete: false` (host.json); the function always ends in an explicit `CompleteMessageAsync` or `DeadLetterMessageAsync` — never a silent return. `CorrelationId` comes from the event, or a generated `Guid` fallback that's logged as a warning.

## 5. Boundaries

- Edit only this repo (`services/processing-func`); don't touch other submodules or the workspace root except as directed.
- `schemas/`, the CloudEvents/message envelope shapes, root `docker-compose.yml` and `.env.example` are shared contracts owned by the Contract Owner role (workspace-root `AGENTS.md` §3) — don't change them here.
- Never commit `local.settings.json`, `.env`, or Azurite runtime data (`azurite-data/`, `__azurite_db_*`, `__blobstorage__/`, `__queuestorage__/`, `AzuriteConfig`).
- Specs live in `specs/NNN-*/` in this repo (spec-kit), not at the workspace root.

## 6. Skills

- `az-cost-optimize` — analyze this repo's `infrastructure/` Bicep (and/or deployed resources) for cost savings; opens GitHub issues per finding.
- `dockerfile` — generate/update this service's `Dockerfile` and its `docker-compose.yml` fragment, keeping local-dev parity with the Azure Container Apps target.
