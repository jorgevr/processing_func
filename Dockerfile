# ---- Build Stage ---------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Copy project files first — layer cache for restore
COPY src/DatasetProcessingFunction/DatasetProcessingFunction.csproj                              src/DatasetProcessingFunction/
COPY src/DatasetProcessingFunction.Domain/DatasetProcessingFunction.Domain.csproj                src/DatasetProcessingFunction.Domain/
COPY src/DatasetProcessingFunction.Application/DatasetProcessingFunction.Application.csproj      src/DatasetProcessingFunction.Application/
COPY src/DatasetProcessingFunction.Infrastructure/DatasetProcessingFunction.Infrastructure.csproj src/DatasetProcessingFunction.Infrastructure/

RUN dotnet restore src/DatasetProcessingFunction/DatasetProcessingFunction.csproj

COPY src/ src/

RUN dotnet publish src/DatasetProcessingFunction/DatasetProcessingFunction.csproj \
    -c Release \
    --no-restore \
    -o /app/publish \
    /p:UseAppHost=false

# ---- Runtime Stage -------------------------------------------------------------
# Azure Functions host for .NET 10 isolated worker
FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated10.0 AS runtime

WORKDIR /home/site/wwwroot

# Non-root user — Functions host writes only to wwwroot and /tmp
RUN groupadd --gid 1001 funcgroup \
    && useradd --uid 1001 --gid funcgroup --shell /bin/bash --no-create-home funcuser \
    && chown -R funcuser:funcgroup /home/site/wwwroot

COPY --from=build --chown=funcuser:funcgroup /app/publish .

# Required by the Functions host when running in a container.
# No secrets here — credentials come from DefaultAzureCredential at runtime:
#   - ACA production : user-assigned managed identity (AZURE_CLIENT_ID only)
#   - Local dev      : AZURE_CLIENT_ID + AZURE_TENANT_ID + AZURE_CLIENT_SECRET via docker-compose
ENV AzureWebJobsScriptRoot=/home/site/wwwroot \
    AzureFunctionsJobHost__Logging__Console__IsEnabled=true \
    DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 80

# /admin/host/ping is unauthenticated and available as soon as the host is ready
HEALTHCHECK --interval=30s --timeout=5s --start-period=45s --retries=3 \
    CMD curl -f http://localhost/admin/host/ping || exit 1

USER funcuser
