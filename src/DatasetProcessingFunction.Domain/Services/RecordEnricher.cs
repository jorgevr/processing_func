using DatasetProcessingFunction.Domain.ValueObjects;

namespace DatasetProcessingFunction.Domain.Services;

public sealed class RecordEnricher
{
    public CanonicalRecord Enrich(
        CanonicalRecord record,
        string datasetId,
        string schemaVersion,
        DateTimeOffset ingestionTime)
    {
        var enrichedFields = new Dictionary<string, object>(record.Fields, StringComparer.OrdinalIgnoreCase)
        {
            ["source_dataset_id"] = datasetId,
            ["ingestion_time"] = ingestionTime,
            ["schema_version"] = schemaVersion
        };

        return record with
        {
            SourceDatasetId = datasetId,
            SchemaVersion = schemaVersion,
            IngestionTime = ingestionTime,
            Fields = enrichedFields
        };
    }
}
