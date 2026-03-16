<!--
SYNC IMPACT REPORT
==================
Version change: 1.1.0 → 1.2.0
Amendment: Added Principle IX — OpenTelemetry Instrumentation Standard;
           strengthened Principle V with OTel mandate; added CI/CD Pipeline
           Governance sub-section to Development Workflow; updated Technology
           Standards table with OpenTelemetry and CI/CD pipeline rows.

Modified principles:
  - Principle V (Observability): OTel telemetryMode in host.json now mandatory;
    OTEL_SERVICE_NAME env var required; correlation via W3C TraceContext mandated
  - Principle VIII (Isolated Worker): startup section updated — OTel wiring added
    alongside App Insights worker service

Added sections:
  - Principle IX: OpenTelemetry Instrumentation Standard (host.json, packages,
    Program.cs configuration, correlation, exporters, forbidden patterns)
  - Development Workflow §8: CI/CD Pipeline Governance (stage gates, slot swap,
    Bicep what-if, test gates, workload identity federation)

Updated sections:
  - Technology Standards: added OpenTelemetry row; updated CI/CD row

Removed sections: N/A

Templates requiring updates:
  - .specify/templates/plan-template.md  ✅ OTel + CI/CD rows in Technical
    Context; Constitution Check updated with OTel gate
  - .specify/templates/spec-template.md  ✅ Observability success criteria hint
    added for OTel trace coverage
  - .specify/templates/tasks-template.md ✅ Phase 2 Foundational now lists OTel
    wiring and CI/CD pipeline setup as expected tasks

Follow-up TODOs:
  - TODO(RATIFICATION_DATE): Confirm exact project kickoff date if different from 2026-03-13.
  - TODO(CI_PLATFORM): Confirm GitHub Actions vs Azure DevOps for pipeline implementation.
-->

# DatasetProcessingFunction Constitution

## Core Principles

### I. Event-Driven Architecture First

All inter-service and cross-bounded-context communication MUST be asynchronous and event-based.
No component may call another component's internal logic directly; integration MUST occur via
published domain events or integration events on a message broker (Azure Service Bus / Event Grid).

- Every dataset ingestion trigger MUST produce a typed domain event.
- Downstream consumers MUST be decoupled from the producer; they subscribe to events, never to
  internal APIs.
- Event contracts (schema + version) are first-class artifacts; changes follow a
  backward-compatible versioning strategy before any breaking change is shipped.
- Dead-letter queues MUST be configured on all subscriptions; unprocessable messages MUST be
  routed to DLQ, not silently dropped.

**Rationale**: Aligns with EventDriven.ReferenceArchitecture. Decoupling enables independent
scaling, replay, and fault isolation — critical for a dataset ingestion pipeline with variable load.

### II. Domain-Driven Design

The domain model is the authoritative source of truth; infrastructure adapts to the domain,
never the reverse.

- Bounded contexts MUST be explicit; the DatasetProcessing context owns its aggregate roots and
  MUST NOT directly mutate aggregates owned by other contexts.
- Aggregates enforce their own invariants; no service layer may bypass aggregate rules via direct
  repository writes.
- Value Objects MUST be immutable; Entities MUST carry a stable identity.
- Ubiquitous language from the ingestion domain MUST be reflected in code identifiers, event
  names, and documentation. Avoid generic names (e.g., `Data`, `Item`, `Manager`).

**Rationale**: DDD prevents accidental complexity and keeps business rules co-located with the
entities they govern, following the EventDriven.ReferenceArchitecture guidance.

### III. CQRS Separation (NON-NEGOTIABLE)

Commands and Queries MUST be implemented as separate handler types; no handler may both mutate
state and return business data in the same operation.

- Command handlers: accept a command, validate, apply business rules via aggregates, emit events.
  They MUST return only an acknowledgement or a typed `CommandResult` — never query data.
- Query handlers: read from a read model (projection or read store); they MUST NOT mutate state.
- Separate read and write models are REQUIRED; a shared mutable model used for both reading and
  writing is a constitution violation.
- MediatR (or equivalent mediator) MUST be used to dispatch commands and queries; direct handler
  instantiation in controllers or functions is prohibited.

**Rationale**: CQRS enables independent scaling of read vs. write paths and makes side-effects
explicit, following EventDriven.ReferenceArchitecture conventions.

### IV. Infrastructure as Code — Bicep

All Azure infrastructure MUST be declared in Bicep files under `infrastructure/`.
No resource may be provisioned manually via the Azure Portal.

- Every service (Function App, Storage Account, Service Bus, Application Insights, Key Vault)
  MUST have a corresponding Bicep module.
- Bicep parameters MUST be externalised via `main.parameters.json`; environment-specific values
  MUST NOT be hard-coded in `.bicep` files.
- `azure.yaml` (AZD manifest) MUST remain the single entry point for `azd up` / `azd provision`.
- Infrastructure changes MUST be reviewed alongside code changes in the same PR.

**Rationale**: Reproducible, auditable environments. Required for the POC-to-production pathway
and to support the Bicep fabric generation requirement.

### V. Observability & Reliability

Every function execution and every domain event handled MUST produce structured telemetry using
the OpenTelemetry standard (see Principle IX for implementation rules).

- **OpenTelemetry is the mandated telemetry protocol.** `host.json` MUST contain
  `"telemetryMode": "OpenTelemetry"` for all function apps.
- Application Insights MUST be the primary telemetry sink via the Azure Monitor OpenTelemetry
  exporter; `ILogger<T>` with structured log properties is the mandatory logging interface —
  no `Console.WriteLine` in production paths.
- All executions MUST propagate trace context via W3C `traceparent` / `tracestate` headers;
  correlation MUST be verifiable end-to-end across host process and worker process.
- All outbound events MUST carry a `CorrelationId` propagated from the triggering request or
  upstream event and linked to the active OpenTelemetry span.
- Retry policies (exponential back-off) MUST be configured on all Service Bus bindings.
- Health probes or availability tests MUST be defined for critical function triggers.
- Alerts on DLQ depth and function error rate MUST be included in the Bicep infrastructure.
- `OTEL_SERVICE_NAME` MUST be set as an application setting in every function app to ensure
  stable node naming in Application Map across slots and environments.

**Rationale**: Observability is non-negotiable for a data ingestion pipeline where silent failures
corrupt downstream datasets. OpenTelemetry provides vendor-neutral, standards-based telemetry
that enables cross-service distributed tracing without lock-in to a single backend.

### VI. Security by Default

Secrets MUST be stored in Azure Key Vault and accessed at runtime via Key Vault references or
the Secrets client; no secret may appear in source code, configuration files, or environment
variables checked into the repository.

- Managed Identity (System-Assigned or User-Assigned) MUST be used for all Azure service
  authentication; connection strings with shared keys are prohibited in production deployments.
- RBAC assignments MUST be declared in Bicep (`Microsoft.Authorization/roleAssignments`);
  no implicit Owner-level access.
- All inbound HTTP-triggered functions MUST enforce authentication (Azure AD / function key at
  minimum for POC; Azure AD preferred).
- Dependency versions MUST be kept current; Dependabot or equivalent MUST be configured.

**Rationale**: Security posture must be established from day one, not retrofitted; Key Vault +
Managed Identity is the Microsoft-recommended baseline for Azure Functions.

### VII. Test-First Quality

Tests MUST be written before implementation code for any non-trivial business logic or
infrastructure wiring.

- **Unit tests** MUST cover all domain aggregate logic, command handlers, and query handlers
  using in-memory fakes (no live Azure dependencies).
- **Integration tests** MUST verify function trigger wiring, event publication, and event
  consumption against real Azure services (or Azurite / Service Bus emulator in CI).
- Tests MUST fail before implementation; the Red-Green-Refactor cycle is enforced.
- Code coverage for the `Domain` and `Application` layers MUST remain ≥ 80%.
- Tests are co-located with the project: `tests/Unit/`, `tests/Integration/`.

**Rationale**: EDA systems have subtle ordering and idempotency bugs that only surface with
proper test coverage; test-first ensures handlers are testable by design.

### VIII. .NET Isolated Worker Implementation Standards

The isolated worker model is the mandated runtime model (Principle I of Technology Standards).
Concrete implementation rules follow directly from Microsoft's official guidance.

#### Startup & Host Initialisation

- `Program.cs` MUST use `FunctionsApplication.CreateBuilder(args)` (`IHostApplicationBuilder`,
  requires `Microsoft.Azure.Functions.Worker` ≥ 2.x) — the `IHostBuilder` / `HostBuilder` pattern
  is legacy and MUST NOT be used in new code.
- OpenTelemetry wiring (Principle IX) MUST be configured in `Program.cs` before
  `builder.Build()` — see Principle IX for the required package and code pattern.
- `AddApplicationInsightsTelemetryWorkerService()` and `ConfigureFunctionsApplicationInsights()`
  MUST NOT be used alongside the OpenTelemetry path; when Principle IX's OTel pattern is in use
  the Azure Monitor exporter replaces the App Insights worker service registration.
- The default Application Insights log-filter rule that restricts `ILogger` to `Warning+` MUST be
  removed in startup so all log levels flow to Application Insights:

  ```csharp
  builder.Logging.Services.Configure<LoggerFilterOptions>(options =>
  {
      var rule = options.Rules.FirstOrDefault(r =>
          r.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
      if (rule is not null) options.Rules.Remove(rule);
  });
  ```

- Extension packages MUST come from `Microsoft.Azure.Functions.Worker.Extensions.*` (isolated
  worker namespace) — in-process `Microsoft.Azure.WebJobs.Extensions.*` packages are prohibited.

#### Dependency Injection & Connection Reuse

- Expensive clients (`ServiceBusClient`, `BlobServiceClient`, `HttpClient`) MUST be registered as
  **Singleton** via DI and injected into function classes via constructor injection. Direct
  `new ServiceBusClient(...)` inside function methods is prohibited.
- `IHttpClientFactory` MUST be used for `HttpClient` instances to avoid socket exhaustion; raw
  `new HttpClient()` inside function scope is prohibited.
- A dedicated Azure Storage account MUST be used per function app; sharing the Functions internal
  storage account (`AzureWebJobsStorage`) with application data is prohibited.

#### Async & Threading

- All function methods MUST be `async Task` (or `async Task<T>`); `void` async functions are
  prohibited.
- `.Result`, `.Wait()`, and `.GetAwaiter().GetResult()` MUST NOT be called on `Task` or
  `ValueTask` — these block threads and cause deadlocks under the Functions host.
- Background `Task` instances started inside a function MUST be awaited before the function
  returns; fire-and-forget tasks are prohibited because site shutdown may preempt them silently.
- Function signatures SHOULD accept `CancellationToken cancellationToken` and propagate it to
  all downstream async calls to support graceful shutdown.

#### Idempotency & Defensive Patterns

- Every Service Bus–triggered function MUST be idempotent: processing the same message twice
  MUST produce the same observable outcome (use a deduplication key stored in the read model or
  a distributed cache).
- Functions MUST use `AutoCompleteMessages = false` on Service Bus bindings and call
  `Complete` / `DeadLetter` explicitly after processing so failures are not silently lost.
- Poison messages that cannot be processed after the configured delivery-count threshold MUST be
  routed to the DLQ; no swallowed exceptions permitted at the trigger boundary.

#### host.json Tuning (Service Bus)

The `host.json` Service Bus extension settings MUST be explicitly configured and reviewed for
every trigger — do not rely on defaults:

```json
{
  "version": "2.0",
  "extensions": {
    "serviceBus": {
      "prefetchCount": 10,
      "messageHandlerOptions": {
        "autoComplete": false,
        "maxConcurrentCalls": 16,
        "maxAutoLockRenewalDuration": "00:05:00"
      }
    }
  }
}
```

- `maxConcurrentCalls` MUST be tuned to the downstream resource capacity (database, downstream
  API) — the default of 1 is intentionally conservative but will be a bottleneck in production.
- `prefetchCount` SHOULD be set to at least `maxConcurrentCalls` to reduce round-trips.
- `autoComplete` MUST be `false` (pairs with the explicit complete/dead-letter requirement above).

#### Deployment

- Function apps MUST be deployed using **Run from Package** (`WEBSITE_RUN_FROM_PACKAGE = 1`)
  to eliminate file-lock contention and enable atomic, restart-free deployments.
- For the Premium hosting plan, a **Warmup trigger** MUST be implemented to pre-load DI
  dependencies and reduce cold-start latency when new instances are added.
- Deployment slots MUST be used for production swap-based deployments (zero downtime);
  direct production deploys without slot swap are prohibited outside of emergency hotfixes.

**Rationale**: These rules translate the Microsoft isolated-worker guidance and Azure Functions
best-practices documentation into non-ambiguous, enforceable constraints that directly support
Principles I (EDA), V (Observability), VI (Security), and VII (Test-First) above. Violations
in any of these areas are a common root cause of silent data loss, thread starvation, and
undetected message-processing failures in Azure Functions workloads.

### IX. OpenTelemetry Instrumentation Standard

OpenTelemetry (OTel) is the mandatory, vendor-neutral instrumentation layer for all services.
No service may emit telemetry exclusively via a proprietary SDK without an OTel abstraction.

#### host.json Configuration (Non-Negotiable)

Every function app's `host.json` MUST include:

```json
{
  "version": "2.0",
  "telemetryMode": "OpenTelemetry"
}
```

When `telemetryMode` is set to `OpenTelemetry`, the `logging.applicationInsights` section of
`host.json` is ignored; log-level filtering is controlled via the `logging.logLevel` block
or language-specific OTel settings.

#### Required NuGet Packages

```text
Microsoft.Azure.Functions.Worker.OpenTelemetry
OpenTelemetry.Extensions.Hosting
Azure.Monitor.OpenTelemetry.Exporter
```

The `Azure.Monitor.OpenTelemetry.Exporter` covers the Application Insights sink. If an
additional OTLP endpoint is required (Grafana, Datadog, etc.), also add
`OpenTelemetry.Exporter.OpenTelemetryProtocol` and configure `OTEL_EXPORTER_OTLP_ENDPOINT`
and `OTEL_EXPORTER_OTLP_HEADERS` as application settings.

#### Program.cs Wiring Pattern

```csharp
builder.Services.AddOpenTelemetry()
    .UseFunctionsWorkerDefaults()   // correlates host + worker traces
    .UseAzureMonitorExporter();     // exports to Application Insights
```

`UseFunctionsWorkerDefaults()` is REQUIRED — it wires the Functions-specific activity source
so that host process and worker process spans share the same `OperationId`. Omitting it breaks
distributed trace correlation between the trigger and function code.

Logging scopes MUST be enabled to propagate structured context into OTel log records:

```csharp
builder.Logging.AddOpenTelemetry(b => b.IncludeScopes = true);
```

#### Application Settings (Required per Function App)

| Setting | Purpose | Example Value |
| ------- | ------- | ------------- |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | App Insights endpoint | `InstrumentationKey=...` |
| `OTEL_SERVICE_NAME` | Stable service name for Application Map | `dataset-processing-func` |

`OTEL_SERVICE_NAME` MUST be set explicitly; never rely on auto-detected defaults which vary
between slots, local dev, and production.

#### Forbidden Patterns

- `ConsoleExporter` MUST NOT be added in production code paths (causes duplicate telemetry).
- `Console.WriteLine` and `Debug.WriteLine` are prohibited in production function code.
- `AddApplicationInsightsTelemetryWorkerService()` MUST NOT co-exist with the OTel path above
  — it causes duplicate request telemetry. Use one pattern exclusively.
- The `Azure Monitor Distro` (`Azure.Monitor.OpenTelemetry.AspNetCore`) MUST NOT be used in
  isolated worker functions — it includes `AspNetCoreInstrumentation` which duplicates request
  spans already emitted by the Functions host.

#### Custom Spans and Metrics

- Significant domain operations (CSV parsing batch, validation run, Bronze write) MUST be
  wrapped in a named OTel Activity span so they appear as child spans in distributed traces.
- Custom business metrics (record count, validation fail count) MUST be emitted via
  `System.Diagnostics.Metrics.Meter` / OTel `Meter` API, not via `TelemetryClient` directly.
- Span and metric names MUST follow the OpenTelemetry semantic conventions naming pattern:
  `{component}.{operation}` (e.g., `dataset.validation.run`, `bronze.write.batch`).

**Rationale**: OpenTelemetry provides a single, vendor-neutral telemetry layer that correlates
traces and logs across the host and worker process boundaries — a critical gap in the legacy
App Insights SDK approach. Standards-based instrumentation also enables export to any
OTLP-compliant backend, supporting future observability tooling changes without code rewrites.

## Technology Standards

| Concern | Mandated Choice | Rationale |
| ------- | -------------- | --------- |
| Runtime | .NET 10 (LTS) Isolated Worker | Current LTS; isolated model preferred for DI flexibility |
| Function Host | Azure Functions v4 | Stable, supports .NET 10 isolated |
| Messaging | Azure Service Bus (Standard/Premium) | Reliable delivery, sessions, DLQ support |
| IaC | Bicep + AZD (`azd up`) | Project requirement; reproducible fabric |
| Mediation | MediatR | CQRS dispatch, consistent with EventDriven.ReferenceArchitecture |
| Logging | `Microsoft.Extensions.Logging` via OTel → App Insights | Structured telemetry, W3C trace correlation (Principle IX) |
| Observability | OpenTelemetry (`telemetryMode: OpenTelemetry` + `Azure.Monitor.OpenTelemetry.Exporter`) | Vendor-neutral; host+worker trace correlation; Principle V & IX |
| Secrets | Azure Key Vault + `DefaultAzureCredential` | Principle VI compliance |
| Testing | xUnit + Moq + Azurite (local) | Consistent with .NET ecosystem |
| CI/CD | GitHub Actions (or Azure DevOps) with `AzureFunctionApp@2` task | TODO(CI_PLATFORM): confirm; slot-swap + test gate + Bicep what-if required |
| Serialisation | `System.Text.Json` (camelCase) | Performance; avoid Newtonsoft unless required |

Deviations from this table require a constitution amendment or an approved complexity-tracking
entry in the feature plan.

## Development Workflow

1. **Feature branch** from `main`; branch name follows `###-short-description`.
2. **Spec first**: run `/speckit.specify` before any implementation.
3. **Constitution Check**: every plan MUST pass the gates defined in the plan template before
   Phase 0 research begins.
4. **Tests first**: unit tests written and confirmed failing before implementation (Principle VII).
5. **PR requirements**:
   - All CI checks pass (build, unit tests, integration tests).
   - Bicep `what-if` output reviewed and attached to PR description.
   - At least one peer review approving constitution compliance.
6. **Squash-merge** to `main`; commit message follows Conventional Commits (`feat:`, `fix:`,
   `infra:`, `docs:`, `test:`).
7. **Post-merge**: `azd up` to target environment triggered via CI/CD pipeline.
8. **CI/CD Pipeline Governance** — the following stage gates are MANDATORY in every pipeline
   and are the enforcement mechanism for this constitution at the delivery level:

   | Stage | Gate | Blocks on failure |
   | ----- | ---- | ----------------- |
   | **Build** | `dotnet build --configuration Release` passes with zero warnings treated as errors | Yes |
   | **Unit Tests** | `dotnet test` (Unit project); coverage report must show Domain + Application ≥ 80% | Yes |
   | **Integration Tests** | `dotnet test` (Integration project) against Azurite / Service Bus emulator | Yes |
   | **Bicep What-If** | `az deployment group what-if` output attached as PR artefact; reviewed before merge | Yes (for infra PRs) |
   | **Deploy to Staging Slot** | `AzureFunctionApp@2` task with `deployToSlotOrASE: true`; staging slot only | Yes |
   | **Smoke / Health Check** | Availability test or health-probe endpoint returns 200 on staging slot | Yes |
   | **Slot Swap** | `AzureAppServiceManage@0` swaps staging → production; only runs after health check passes | Yes |
   | **Observability Validation** | Post-deploy step queries App Insights for at least one `OTEL_SERVICE_NAME`-tagged trace within 2 min of deployment | Warning (non-blocking) |

   - Pipeline MUST authenticate to Azure using **Workload Identity Federation** (OIDC) —
     long-lived service principal secrets in CI variables are prohibited (Principle VI).
   - Direct production deploys without staging slot swap are prohibited outside of emergency
     hotfixes (must be documented in `Complexity Tracking` of the relevant plan).
   - The `AzureFunctionApp@2` task MUST be used; `@v1` is prohibited for new pipelines.
   - Pipeline YAML files MUST reside in `.github/workflows/` (GitHub Actions) or
     `.azure-pipelines/` (Azure DevOps) and are subject to the same PR review process
     as source code.

## Governance

This constitution supersedes all other project conventions, README guidance, and verbal agreements.
Amendments require:

1. A draft PR updating `.specify/memory/constitution.md` with a version bump.
2. A Sync Impact Report (HTML comment at top of file) describing changes.
3. Approval from at least one domain lead.
4. Propagation of changes to all affected templates (`.specify/templates/`) in the same PR.

**Versioning policy**: semantic versioning (MAJOR.MINOR.PATCH) as defined in the speckit
constitution command workflow.

**Compliance review**: Constitution compliance MUST be checked at PR review time using the
`Constitution Check` gate in `plan-template.md`. Violations MUST be documented in the
`Complexity Tracking` table of the relevant plan with justification.

**Version**: 1.2.0 | **Ratified**: 2026-03-13 | **Last Amended**: 2026-03-15
