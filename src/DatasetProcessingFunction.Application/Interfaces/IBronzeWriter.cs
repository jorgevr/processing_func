using DatasetProcessingFunction.Domain.ValueObjects;

namespace DatasetProcessingFunction.Application.Interfaces;

/// <summary>Writes canonical records to the Fabric Lakehouse Bronze layer as Parquet.</summary>
public interface IBronzeWriter
{
    /// <summary>
    /// Serializes <paramref name="records"/> to Parquet and writes them to the Bronze partition
    /// for <paramref name="id"/> and <paramref name="date"/>. The write is idempotent: any
    /// existing file at the target path is deleted before upload.
    /// </summary>
    /// <param name="id">Unique dataset identifier used to construct the Bronze partition path.</param>
    /// <param name="date">Processing date used as the partition date component.</param>
    /// <param name="records">Canonical records to serialize.</param>
    /// <param name="cancellationToken">Propagated cancellation token.</param>
    /// <returns>The fully-qualified ADLS URI of the written Parquet file.</returns>
    Task<Uri> WriteAsync(
        DatasetId id,
        DateOnly date,
        IReadOnlyList<CanonicalRecord> records,
        CancellationToken cancellationToken = default);
}
