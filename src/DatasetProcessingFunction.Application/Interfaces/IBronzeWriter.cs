using DatasetProcessingFunction.Domain.Models;
using DatasetProcessingFunction.Domain.ValueObjects;

namespace DatasetProcessingFunction.Application.Interfaces;

/// <summary>Writes canonical records to the Fabric Lakehouse Bronze layer as Parquet.</summary>
public interface IBronzeWriter
{
    /// <summary>
    /// Serializes <paramref name="records"/> to Parquet and writes them to the Bronze partition
    /// for <paramref name="id"/> and <paramref name="date"/>. The write is idempotent: any
    /// existing file at the target path is deleted before upload.
    /// The Parquet schema is built dynamically from <paramref name="mapping"/> (FR-017b).
    /// </summary>
    Task<Uri> WriteAsync(
        DatasetId id,
        DateOnly date,
        IReadOnlyList<CanonicalRecord> records,
        VendorSchemaMapping mapping,
        CancellationToken cancellationToken = default);
}
