using Azure.Storage.Files.DataLake;
using DatasetProcessingFunction.Application.Interfaces;
using DatasetProcessingFunction.Domain.Models;
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

    // Enrichment column names that take precedence over vendor-mapped columns with the same name
    private static readonly HashSet<string> EnrichmentNames = new(StringComparer.OrdinalIgnoreCase)
        { "site_id", "timestamp", "ingestion_time", "source_dataset_id", "schema_version" };

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
        VendorSchemaMapping mapping,
        CancellationToken cancellationToken = default)
    {
        var partitionPath = $"{id.Value}/{date:yyyy-MM-dd}/data.parquet";
        _logger.LogInformation("Writing {Count} records to Bronze path: {Path}", records.Count, partitionPath);

        var fileSystemClient = _serviceClient.GetFileSystemClient(_fileSystemName);
        var fileClient = fileSystemClient.GetFileClient(partitionPath);

        // Idempotent overwrite — delete existing partition first (FR-018)
        await fileClient.DeleteIfExistsAsync(cancellationToken: cancellationToken);

        using var parquetStream = new MemoryStream();
        await WriteParquetAsync(records, mapping, parquetStream, cancellationToken);
        parquetStream.Position = 0;

        // Single atomic upload — failed upload leaves previous partition intact (FR-019)
        await fileClient.UploadAsync(parquetStream, overwrite: true, cancellationToken);

        var bronzeUri = BuildBronzeUri(partitionPath);
        _logger.LogInformation("Bronze write complete: {Uri}", bronzeUri);
        return bronzeUri;
    }

    private static async Task WriteParquetAsync(
        IReadOnlyList<CanonicalRecord> records,
        VendorSchemaMapping mapping,
        Stream output,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0) return;

        var passThroughCols = GetPassThroughColumnNames(mapping, records);
        var schema = BuildSchema(mapping, records);

        using var writer = await ParquetWriter.CreateAsync(schema, output);
        using var rowGroup = writer.CreateRowGroup();

        // Write enrichment columns first (indices 0–4)
        await rowGroup.WriteColumnAsync(new DataColumn(schema.DataFields[0],
            records.Select(r => r.SiteId).ToArray()));
        await rowGroup.WriteColumnAsync(new DataColumn(schema.DataFields[1],
            records.Select(r => (DateTime?)r.Timestamp.UtcDateTime).ToArray()));
        await rowGroup.WriteColumnAsync(new DataColumn(schema.DataFields[2],
            records.Select(r => (DateTime?)r.IngestionTime.UtcDateTime).ToArray()));
        await rowGroup.WriteColumnAsync(new DataColumn(schema.DataFields[3],
            records.Select(r => r.SourceDatasetId).ToArray()));
        await rowGroup.WriteColumnAsync(new DataColumn(schema.DataFields[4],
            records.Select(r => r.SchemaVersion).ToArray()));

        // Write vendor-mapped columns (indices 5..5+N-1), in schema order
        var vendorCols = mapping.ColumnMappings
            .Where(c => !EnrichmentNames.Contains(c.CanonicalField))
            .ToList();

        for (var i = 0; i < vendorCols.Count; i++)
        {
            var col = vendorCols[i];
            var array = BuildColumnArray(records, col.CanonicalField, col.DataType);
            await rowGroup.WriteColumnAsync(new DataColumn(schema.DataFields[5 + i], array));
        }

        // Write pass-through columns as raw strings (indices after mapped vendor columns)
        for (var i = 0; i < passThroughCols.Count; i++)
        {
            var array = BuildColumnArray(records, passThroughCols[i], "string");
            await rowGroup.WriteColumnAsync(new DataColumn(schema.DataFields[5 + vendorCols.Count + i], array));
        }
    }

    /// <summary>
    /// Builds a <see cref="ParquetSchema"/> from the five fixed enrichment columns plus one
    /// column per entry in <paramref name="mapping"/>.ColumnMappings (duplicates of enrichment
    /// names are skipped). When <paramref name="mapping"/>.PassThroughUnmapped is true and
    /// <paramref name="records"/> are provided, any extra fields found in the first record that
    /// are not covered by the mapping or enrichment columns are appended as nullable strings.
    /// Per research.md §1: use <see cref="DateTimeDataField"/> for datetime (INT64 millis, not
    /// INT96) and <see cref="DecimalDataField"/> for decimal (explicit precision).
    /// </summary>
    internal static ParquetSchema BuildSchema(
        VendorSchemaMapping mapping,
        IReadOnlyList<CanonicalRecord>? records = null)
    {
        var fields = new List<Field>
        {
            // Fixed enrichment columns — non-nullable
            new DataField<string>("site_id"),
            new DateTimeDataField("timestamp",      DateTimeFormat.DateAndTime, isNullable: true),
            new DateTimeDataField("ingestion_time", DateTimeFormat.DateAndTime, isNullable: true),
            new DataField<string>("source_dataset_id"),
            new DataField<string>("schema_version"),
        };

        foreach (var col in mapping.ColumnMappings.Where(c => !EnrichmentNames.Contains(c.CanonicalField)))
            fields.Add(CreateVendorField(col.CanonicalField, col.DataType));

        if (records is not null)
        {
            foreach (var name in GetPassThroughColumnNames(mapping, records))
                fields.Add(new DataField<string>(name));
        }

        return new ParquetSchema(fields);
    }

    private static IReadOnlyList<string> GetPassThroughColumnNames(
        VendorSchemaMapping mapping, IReadOnlyList<CanonicalRecord> records)
    {
        if (!mapping.PassThroughUnmapped || records.Count == 0)
            return [];

        var knownFields = mapping.ColumnMappings
            .Select(c => c.CanonicalField)
            .Concat(EnrichmentNames)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return records[0].Fields.Keys
            .Where(k => !knownFields.Contains(k))
            .ToList();
    }

    private static Field CreateVendorField(string canonicalName, string dataType) =>
        dataType.ToLowerInvariant() switch
        {
            "datetime" => new DateTimeDataField(canonicalName,
                                           DateTimeFormat.DateAndTime, isNullable: true),
            "decimal" => new DecimalDataField(canonicalName,
                                           precision: 18, scale: 6, isNullable: true),
            "double" => new DataField(canonicalName, typeof(double?), isNullable: true),
            "float" => new DataField(canonicalName, typeof(float?), isNullable: true),
            "int" or "integer" => new DataField(canonicalName, typeof(int?), isNullable: true),
            "long" => new DataField(canonicalName, typeof(long?), isNullable: true),
            "bool" or "boolean" => new DataField(canonicalName, typeof(bool?), isNullable: true),
            _ => new DataField<string>(canonicalName),  // string: ref type, always nullable
        };

    /// <summary>
    /// Extracts a typed array from <see cref="CanonicalRecord.Fields"/> for the given column,
    /// matching the CLR type expected by the corresponding <see cref="DataField"/> (research.md §1).
    /// </summary>
    internal static Array BuildColumnArray(
        IReadOnlyList<CanonicalRecord> records, string fieldName, string dataType) =>
        dataType.ToLowerInvariant() switch
        {
            "decimal" => records.Select(r =>
                r.Fields.TryGetValue(fieldName, out var v) && v is decimal d ? (decimal?)d
                : r.Fields.TryGetValue(fieldName, out v) && v is not null
                    ? (decimal?)Convert.ToDecimal(v, System.Globalization.CultureInfo.InvariantCulture)
                    : null).ToArray(),

            "datetime" => records.Select(r =>
                r.Fields.TryGetValue(fieldName, out var v)
                    ? v is DateTimeOffset dto ? (DateTime?)dto.UtcDateTime
                    : v is DateTime dt ? (DateTime?)dt
                    : null
                    : null).ToArray(),

            "double" => records.Select(r =>
                r.Fields.TryGetValue(fieldName, out var v) && v is not null
                    ? (double?)Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture)
                    : null).ToArray(),

            "float" => records.Select(r =>
                r.Fields.TryGetValue(fieldName, out var v) && v is not null
                    ? (float?)Convert.ToSingle(v, System.Globalization.CultureInfo.InvariantCulture)
                    : null).ToArray(),

            "int" or "integer" => records.Select(r =>
                r.Fields.TryGetValue(fieldName, out var v) && v is not null
                    ? (int?)Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture)
                    : null).ToArray(),

            "long" => records.Select(r =>
                r.Fields.TryGetValue(fieldName, out var v) && v is not null
                    ? (long?)Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture)
                    : null).ToArray(),

            "bool" or "boolean" => records.Select(r =>
                r.Fields.TryGetValue(fieldName, out var v) && v is bool b ? (bool?)b : null).ToArray(),

            _ => records.Select(r =>
                r.Fields.TryGetValue(fieldName, out var v) ? v?.ToString() : null).ToArray(),
        };

    private Uri BuildBronzeUri(string partitionPath)
    {
        if (!string.IsNullOrWhiteSpace(_onelakeEndpoint))
            return new Uri($"{_onelakeEndpoint.TrimEnd('/')}/{_fileSystemName}/{partitionPath}");

        var accountUri = _serviceClient.Uri;
        return new Uri($"{accountUri}{_fileSystemName}/{partitionPath}");
    }
}
