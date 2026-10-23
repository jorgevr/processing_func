using System.Reflection;
using System.Text.Json;
using DatasetProcessingFunction.Application.Interfaces;
using Json.Schema;

namespace DatasetProcessingFunction.Infrastructure.Contracts;

/// <summary>
/// Validates an inbound <c>solar.pvdaq.dataset.available.v1</c> CloudEvent envelope against the
/// vendored <c>contracts/dataset-available.v1.json</c> (R3.7, ADR 0002, ADR 0004 rule 3).
/// </summary>
/// <remarks>
/// R3.7b: the vendored schema is loaded, parsed and sanity-checked (readable, valid JSON Schema,
/// <c>$id</c> matches what this validator expects) eagerly, in the constructor — see
/// <see cref="DatasetAvailableEventValidator()"/>. Program.cs constructs this instance directly
/// (<c>new DatasetAvailableEventValidator()</c>) before registering it, so a missing, unreadable,
/// malformed or mismatched vendored copy fails host start with a clear message naming the schema
/// and the cause, instead of surfacing opaquely on the first inbound message.
/// </remarks>
public sealed class DatasetAvailableEventValidator : IDatasetEventValidator
{
    internal const string EmbeddedResourceName =
        "DatasetProcessingFunction.Infrastructure.Contracts.dataset-available.v1.json";

    /// <summary>
    /// The vendored schema's own declared <c>$id</c> — also the exact value the contract's
    /// <c>dataschema</c> field asserts via <c>const</c> (contracts/dataset-available.v1.json).
    /// Checked at load time so a vendored copy that is valid JSON Schema but is the wrong
    /// version, stale, or simply the wrong file is caught at startup, not accepted silently.
    /// </summary>
    internal const string ExpectedSchemaId =
        "https://github.com/jorgevr/anomalIA-app/contracts/dataset-available.v1.json";

    // Parsed exactly once per process: JsonSchema.Net registers a schema's $id in a process-wide
    // registry, and parsing the same $id twice throws. A `static readonly` cache — not a per-call
    // or per-instance load — is what guarantees "exactly once" here, independent of how many
    // DatasetAvailableEventValidator instances DI (or tests) create.
    //
    // This is a Lazy<T>, not a bare static field initializer, specifically so the factory runs
    // inside Lazy<T>.Value — a normal method call — rather than inside the type's static
    // constructor. A static field initializer compiles into the static constructor, and the CLR
    // unconditionally wraps any exception thrown there in an opaque TypeInitializationException;
    // routing the same failure through Lazy<T>.Value instead lets the original, clearly-worded
    // exception (naming the schema and the cause) propagate unwrapped. Lazy<T>'s default mode
    // also caches a thrown exception and rethrows the SAME one on every later access — exactly
    // the "no retry" behaviour this task requires, for free.
    private static readonly Lazy<JsonSchema> SchemaLazy = new(
        () => LoadFromEmbeddedResource(typeof(DatasetAvailableEventValidator).Assembly, EmbeddedResourceName));

    public DatasetAvailableEventValidator()
    {
        // Eager (R3.7b): force the load+parse+$id check NOW, synchronously, in the constructor —
        // not whenever Validate() first happens to run. This type has no explicit static
        // constructor, so the CLR treats it as `beforefieldinit`: constructing an instance that
        // never reads a static field does NOT guarantee the field's initializer has run (verified
        // directly — a throwaway repro of exactly this shape never threw). The explicit read below
        // is what forces it.
        _ = SchemaLazy.Value;
    }

    private static JsonSchema Schema => SchemaLazy.Value;

    /// <summary>
    /// Loads, parses and sanity-checks the vendored schema from an embedded resource. Exposed
    /// internally (not private) so tests can exercise the missing/unreadable/invalid-schema
    /// failure paths directly — with an intentionally wrong resource name against this same real
    /// assembly, or a throwing stream — without depending on the workspace layout or rebuilding
    /// the assembly with a broken resource.
    /// </summary>
    internal static JsonSchema LoadFromEmbeddedResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Vendored contract schema '{resourceName}' is missing: no embedded resource with " +
                $"that name exists in assembly '{assembly.GetName().Name}'.");

        return LoadFromStream(stream, resourceName);
    }

    /// <summary>Reads <paramref name="stream"/> fully and parses it, for the same reason as above.</summary>
    internal static JsonSchema LoadFromStream(Stream stream, string schemaSourceName)
    {
        string rawText;
        try
        {
            using var reader = new StreamReader(stream);
            rawText = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Vendored contract schema '{schemaSourceName}' could not be read: {ex.Message}", ex);
        }

        return ParseAndValidate(rawText, schemaSourceName);
    }

    /// <summary>Parses raw schema text and checks its <c>$id</c>, for the same reason as above.</summary>
    internal static JsonSchema ParseAndValidate(string rawSchemaText, string schemaSourceName)
    {
        JsonSchema schema;
        try
        {
            schema = JsonSchema.FromText(rawSchemaText);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Vendored contract schema '{schemaSourceName}' is not a valid JSON Schema: {ex.Message}", ex);
        }

        var actualId = schema.BaseUri?.ToString();
        if (!string.Equals(actualId, ExpectedSchemaId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Vendored contract schema '{schemaSourceName}' has $id '{actualId}', but this " +
                $"validator expects '{ExpectedSchemaId}'. The vendored copy may be stale, " +
                "corrupted, or swapped for the wrong contract.");
        }

        return schema;
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
