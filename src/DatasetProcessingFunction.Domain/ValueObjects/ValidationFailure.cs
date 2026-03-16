namespace DatasetProcessingFunction.Domain.ValueObjects;

public sealed record ValidationFailure(
    int RowIndex,
    string FieldName,
    string Rule,
    string Detail);
