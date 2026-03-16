namespace DatasetProcessingFunction.Application.Interfaces;

/// <summary>Reads a raw dataset file from ADLS Gen2 or an OneLake-compatible storage endpoint.</summary>
public interface IDatasetReader
{
    /// <summary>
    /// Opens and returns a readable stream for the dataset at <paramref name="storagePath"/>.
    /// The caller is responsible for disposing the returned stream.
    /// </summary>
    /// <param name="storagePath">Fully-qualified ADLS or HTTP URI (e.g. <c>abfss://…</c>).</param>
    /// <param name="cancellationToken">Propagated cancellation token.</param>
    /// <returns>A readable, forward-only stream containing the raw file bytes.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the URI scheme is not supported.</exception>
    Task<Stream> ReadAsync(Uri storagePath, CancellationToken cancellationToken = default);
}
