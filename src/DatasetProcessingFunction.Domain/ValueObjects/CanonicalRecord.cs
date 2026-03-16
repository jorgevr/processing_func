namespace DatasetProcessingFunction.Domain.ValueObjects;

public sealed record CanonicalRecord(
    string SiteId,
    DateTimeOffset Timestamp,
    DateTimeOffset IngestionTime,
    string SourceDatasetId,
    string SchemaVersion,
    IReadOnlyDictionary<string, object> Fields);
