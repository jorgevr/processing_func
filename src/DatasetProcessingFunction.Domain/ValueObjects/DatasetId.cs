namespace DatasetProcessingFunction.Domain.ValueObjects;

public sealed record DatasetId
{
    public string Value { get; }

    public DatasetId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("DatasetId value cannot be null or empty.", nameof(value));
        Value = value;
    }

    public override string ToString() => Value;
}
