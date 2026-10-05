using System.Text.Json;
using DatasetProcessingFunction.Application.Interfaces;
using Json.Schema;

namespace DatasetProcessingFunction.Infrastructure.Contracts;

/// <summary>
/// Validates an inbound <c>solar.pvdaq.dataset.available.v1</c> CloudEvent envelope against the
/// vendored <c>contracts/dataset-available.v1.json</c> (R3.7, ADR 0002, ADR 0004 rule 3).
/// </summary>
public sealed class DatasetAvailableEventValidator : IDatasetEventValidator
{
    // Parsed exactly once per process: JsonSchema.Net registers a schema's $id in a process-wide
    // registry, and parsing the same $id twice throws. A `static readonly` field — not a per-call
    // or per-instance load — is what guarantees "exactly once" here, independent of how many
    // DatasetAvailableEventValidator instances DI creates.
    private static readonly JsonSchema Schema = LoadSchema();

    private static JsonSchema LoadSchema()
    {
        var assembly = typeof(DatasetAvailableEventValidator).Assembly;
        const string resourceName =
            "DatasetProcessingFunction.Infrastructure.Contracts.dataset-available.v1.json";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded contract resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    public DatasetEventValidationResult Validate(string rawJson)
    {
        using var document = JsonDocument.Parse(rawJson); // throws JsonException on malformed JSON

        var actualType = document.RootElement.TryGetProperty("type", out var typeElement)
            && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;

        if (!string.Equals(actualType, IDatasetEventValidator.ExpectedType, StringComparison.Ordinal))
            return DatasetEventValidationResult.UnrecognizedType(actualType);

        var results = Schema.Evaluate(document.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        if (results.IsValid)
            return DatasetEventValidationResult.Valid();

        // Root-level (InstanceLocation == "") details only. JsonSchema.Net's List output also
        // reports leaf "pattern" failures that occur *inside* a correctly-failing `not` branch
        // (the traceparent $def's allOf/not pair) even when the overall document is valid — those
        // are internal evaluation noise, not real problems, and every genuine failure is already
        // named at the root (e.g. "properties: ... [\"traceparent\"]" / "required: ... [\"dataschema\"]").
        var errors = (results.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 } && d.InstanceLocation.ToString().Length == 0)
            .SelectMany(d => d.Errors!.Select(e => $"{e.Key}: {e.Value}"))
            .ToList();

        return DatasetEventValidationResult.Invalid(errors);
    }
}
