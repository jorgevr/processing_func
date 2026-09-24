@AGENTS.md

# processing_func Development Guidelines

Auto-generated from all feature plans. Last updated: 2026-03-15

## Active Technologies
- .NET 10 (LTS) Isolated Worker + MediatR 12.x, CsvHelper 33.x, Parquet.Net 4.23.x, Azure.Storage.Files.DataLake 12.18.x, Azure.Storage.Blobs 12.x, Azure.Messaging.ServiceBus 7.18.x, OpenTelemetry 1.10.x, Microsoft.Extensions.Resilience (Polly v8) (main)
- ADLS Gen2 (`DataLakeServiceClient`) for input reads + Bronze writes; Azure Blob Storage for schema registry; Azurite + Microsoft Service Bus Emulator (Docker) for local dev (main)

## Project Structure

```text
src/
tests/
```

## Recent Changes
- 002-dataset-ingestion-pipeline: Added .NET 10 (LTS) Isolated Worker + MediatR 12.x, CsvHelper 33.x, Parquet.Net 4.23.x, Azure.Storage.Files.DataLake 12.18.x, Azure.Storage.Blobs 12.x, Azure.Messaging.ServiceBus 7.18.x, OpenTelemetry 1.10.x, Microsoft.Extensions.Resilience (Polly v8)
- 001-dataset-ingestion-pipeline: Added .NET 10 (LTS) Isolated Worker + MediatR 12.x, CsvHelper 33.x, Parquet.Net 4.23.x, Azure.Storage.Files.DataLake 12.18.x, Azure.Messaging.ServiceBus 7.18.x, OpenTelemetry 1.10.x

<!-- MANUAL ADDITIONS START -->
<!-- MANUAL ADDITIONS END -->
