namespace DatasetProcessingFunction.Domain.Enums;

public enum ProcessingStatus
{
    Received,
    Parsing,
    Validating,
    Transforming,
    Writing,
    Completed,
    Failed
}
