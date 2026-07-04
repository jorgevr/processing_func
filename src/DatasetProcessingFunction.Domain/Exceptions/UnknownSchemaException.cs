namespace DatasetProcessingFunction.Domain.Exceptions;

/// <summary>
/// Thrown by <see cref="DatasetProcessingFunction.Infrastructure.SchemaRegistry.BlobSchemaRegistry"/>
/// when no mapping exists for the given vendor/version, or when a mapping exists but is structurally
/// invalid (missing required canonical fields: site_id or timestamp — FR-016, FR-016a).
/// The event must be dead-lettered with reason <c>UnknownSchema</c>.
/// </summary>
public sealed class UnknownSchemaException : Exception
{
    public string VendorId { get; }
    public string SchemaVersion { get; }

    public UnknownSchemaException(string vendorId, string schemaVersion)
        : base($"No valid schema mapping found for vendor '{vendorId}' with version '{schemaVersion}'.")
    {
        VendorId = vendorId;
        SchemaVersion = schemaVersion;
    }
}
