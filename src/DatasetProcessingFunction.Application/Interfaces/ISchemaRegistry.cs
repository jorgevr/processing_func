using DatasetProcessingFunction.Domain.Models;

namespace DatasetProcessingFunction.Application.Interfaces;

/// <summary>Resolves vendor schema mappings from a persistent registry (e.g. Azure Blob Storage).</summary>
public interface ISchemaRegistry
{
    /// <summary>
    /// Retrieves the schema mapping for the given <paramref name="vendorId"/> and
    /// <paramref name="schemaVersion"/>. Returns <see langword="null"/> when no matching mapping
    /// is registered, which the caller should treat as an unknown-schema error.
    /// Implementations should cache results for the lifetime of the host instance.
    /// </summary>
    /// <param name="vendorId">Vendor identifier (e.g. <c>vendor-abc</c>).</param>
    /// <param name="schemaVersion">Schema version string (e.g. <c>v2</c>).</param>
    /// <param name="cancellationToken">Propagated cancellation token.</param>
    /// <returns>The <see cref="VendorSchemaMapping"/>, or <see langword="null"/> if not found.</returns>
    Task<VendorSchemaMapping?> GetAsync(
        string vendorId,
        string schemaVersion,
        CancellationToken cancellationToken = default);
}
