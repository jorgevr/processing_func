namespace DatasetProcessingFunction.Application.Commands;

public sealed record ProcessDatasetResult(
    bool Success,
    int RecordsProcessed,
    Uri? BronzePath,
    string? ErrorMessage)
{
    public static ProcessDatasetResult Ok(int recordsProcessed, Uri bronzePath) =>
        new(true, recordsProcessed, bronzePath, null);

    public static ProcessDatasetResult Fail(string errorMessage) =>
        new(false, 0, null, errorMessage);
}
