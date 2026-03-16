using Azure.Storage.Files.DataLake;
using DatasetProcessingFunction.Application.Interfaces;
using DatasetProcessingFunction.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace DatasetProcessingFunction.Infrastructure.Storage;

public sealed class OneLakeBronzeWriter : IBronzeWriter
{
    private readonly DataLakeServiceClient _serviceClient;
    private readonly string _fileSystemName;
    private readonly string? _onelakeEndpoint;
    private readonly ILogger<OneLakeBronzeWriter> _logger;

    public OneLakeBronzeWriter(
        DataLakeServiceClient serviceClient,
        string fileSystemName,
        string? onelakeEndpoint,
        ILogger<OneLakeBronzeWriter> logger)
    {
        _serviceClient = serviceClient ?? throw new ArgumentNullException(nameof(serviceClient));
        _fileSystemName = fileSystemName ?? throw new ArgumentNullException(nameof(fileSystemName));
        _onelakeEndpoint = onelakeEndpoint;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Uri> WriteAsync(
        DatasetId id,
        DateOnly date,
        IReadOnlyList<CanonicalRecord> records,
        CancellationToken cancellationToken = default)
    {
        var partitionPath = $"{id.Value}/{date:yyyy-MM-dd}/data.parquet";
        _logger.LogInformation("Writing {Count} records to Bronze path: {Path}", records.Count, partitionPath);

        var fileSystemClient = _serviceClient.GetFileSystemClient(_fileSystemName);
        var fileClient = fileSystemClient.GetFileClient(partitionPath);

        // Idempotent overwrite — delete existing partition first
        await fileClient.DeleteIfExistsAsync(cancellationToken: cancellationToken);

        using var parquetStream = new MemoryStream();
        await WriteParquetAsync(records, parquetStream, cancellationToken);
        parquetStream.Position = 0;

        await fileClient.UploadAsync(parquetStream, overwrite: true, cancellationToken);

        var bronzeUri = BuildBronzeUri(partitionPath);
        _logger.LogInformation("Bronze write complete: {Uri}", bronzeUri);
        return bronzeUri;
    }

    private static async Task WriteParquetAsync(
        IReadOnlyList<CanonicalRecord> records,
        Stream output,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0) return;

        // Build schema from well-known canonical fields
        var schema = new ParquetSchema(
            new DataField<string>("site_id"),
            new DataField<DateTimeOffset>("timestamp"),
            new DataField<DateTimeOffset>("ingestion_time"),
            new DataField<string>("source_dataset_id"),
            new DataField<string>("schema_version")
        );

        using var writer = await ParquetWriter.CreateAsync(schema, output);
        using var rowGroup = writer.CreateRowGroup();

        var siteIds = records.Select(r => r.SiteId).ToArray();
        var timestamps = records.Select(r => r.Timestamp).ToArray();
        var ingestionTimes = records.Select(r => r.IngestionTime).ToArray();
        var sourceDatasetIds = records.Select(r => r.SourceDatasetId).ToArray();
        var schemaVersions = records.Select(r => r.SchemaVersion).ToArray();

        await rowGroup.WriteColumnAsync(new DataColumn(schema.DataFields[0], siteIds));
        await rowGroup.WriteColumnAsync(new DataColumn(schema.DataFields[1], timestamps));
        await rowGroup.WriteColumnAsync(new DataColumn(schema.DataFields[2], ingestionTimes));
        await rowGroup.WriteColumnAsync(new DataColumn(schema.DataFields[3], sourceDatasetIds));
        await rowGroup.WriteColumnAsync(new DataColumn(schema.DataFields[4], schemaVersions));
    }

    private Uri BuildBronzeUri(string partitionPath)
    {
        if (!string.IsNullOrWhiteSpace(_onelakeEndpoint))
            return new Uri($"{_onelakeEndpoint.TrimEnd('/')}/{_fileSystemName}/{partitionPath}");

        var accountUri = _serviceClient.Uri;
        return new Uri($"{accountUri}{_fileSystemName}/{partitionPath}");
    }
}
