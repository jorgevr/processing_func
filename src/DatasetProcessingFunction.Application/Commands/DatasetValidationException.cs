using DatasetProcessingFunction.Domain.ValueObjects;

namespace DatasetProcessingFunction.Application.Commands;

public sealed class DatasetValidationException : Exception
{
    public string DatasetId { get; }
    public ValidationResult ValidationResult { get; }

    public DatasetValidationException(string datasetId, ValidationResult validationResult)
        : base($"Dataset '{datasetId}' failed validation with {validationResult.FailCount} failure(s).")
    {
        DatasetId = datasetId;
        ValidationResult = validationResult;
    }
}
