namespace DatasetProcessingFunction.Application.Interfaces;

/// <summary>
/// Validates a raw inbound CloudEvent envelope against the vendored
/// <c>dataset-available.v1.json</c> contract (ADR 0002, ADR 0004) before it is deserialised into a
/// domain model. The envelope is closed (an unknown envelope attribute fails); <c>data</c> is open
/// (an unknown <c>data</c> field — e.g. ADR 0006's future <c>device_id</c> — must still validate).
/// </summary>
public interface IDatasetEventValidator
{
    /// <summary>The only <c>type</c> value a validated envelope may carry (ADR 0002).</summary>
    const string ExpectedType = "solar.pvdaq.dataset.available.v1";

    /// <summary>
    /// Validates <paramref name="rawJson"/> against the vendored contract. <c>type</c> is checked
    /// first and reported distinctly from other envelope violations (ADR 0002 rule 5 — the consumer
    /// must assert <c>type</c>; a wrong type and a malformed envelope are different operator
    /// stories), even though the contract's own <c>const</c> on <c>type</c> would reject it too.
    /// </summary>
    /// <exception cref="System.Text.Json.JsonException">
    /// <paramref name="rawJson"/> is not well-formed JSON at all — the caller should treat this the
    /// same as any other deserialization failure.
    /// </exception>
    DatasetEventValidationResult Validate(string rawJson);
}

/// <summary>Outcome of <see cref="IDatasetEventValidator.Validate"/>.</summary>
public sealed record DatasetEventValidationResult
{
    public bool IsValid { get; private init; }
    public bool IsUnrecognizedType { get; private init; }
    public string? ActualType { get; private init; }
    public IReadOnlyList<string> Errors { get; private init; } = [];

    public static DatasetEventValidationResult Valid() => new() { IsValid = true };

    public static DatasetEventValidationResult UnrecognizedType(string? actualType) =>
        new() { IsValid = false, IsUnrecognizedType = true, ActualType = actualType };

    public static DatasetEventValidationResult Invalid(IReadOnlyList<string> errors) =>
        new() { IsValid = false, Errors = errors };
}
