namespace DatasetProcessingFunction.Domain.ValueObjects;

public sealed record RawRecord(
    int RowIndex,
    IReadOnlyDictionary<string, string> Fields);
