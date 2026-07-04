namespace DatasetProcessingFunction.Domain.Exceptions;

/// <summary>
/// Thrown by <see cref="DatasetProcessingFunction.Domain.Services.CsvParserService"/> when a CSV file
/// contains a header row but zero data rows (FR-006c).
/// The event must be dead-lettered with reason <c>EmptyDataset</c> — no Bronze write is performed.
/// </summary>
public sealed class EmptyDatasetException : Exception
{
    public EmptyDatasetException()
        : base("The CSV file contains a header row but no data rows.") { }

    public EmptyDatasetException(string message) : base(message) { }
}
