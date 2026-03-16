namespace DatasetProcessingFunction.Domain.ValueObjects;

public sealed record ProcessingMetrics(
    string DatasetId,
    int RecordsProcessed,
    int ValidationPassCount,
    int ValidationFailCount,
    long ProcessingDurationMs,
    Uri? BronzePath);
