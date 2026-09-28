using System.IO;
using Azure;
using Azure.Storage.Blobs;
using DatasetProcessingFunction.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Registry;

namespace DatasetProcessingFunction.Infrastructure.Storage;

public sealed class AdlsDatasetReader : IDatasetReader
{
    private readonly BlobServiceClient _serviceClient;
    private readonly ResiliencePipeline _pipeline;
    private readonly ILogger<AdlsDatasetReader> _logger;

    public AdlsDatasetReader(
        BlobServiceClient serviceClient,
        ResiliencePipelineProvider<string> pipelineProvider,
        ILogger<AdlsDatasetReader> logger)
    {
        _serviceClient = serviceClient ?? throw new ArgumentNullException(nameof(serviceClient));
        _pipeline = pipelineProvider.GetPipeline("adls-read");
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Stream> ReadAsync(Uri storagePath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Reading dataset from {StoragePath}", storagePath);

        var (containerName, blobName) = ParseStoragePath(storagePath);
        var blobClient = _serviceClient
            .GetBlobContainerClient(containerName)
            .GetBlobClient(blobName);

        // Polly handles transient retries (IOException, RequestFailedException 429/500/503/408).
        // Cancellation during backoff wait propagates immediately without consuming a retry attempt.
        // After retry exhaustion Polly re-throws the last exception — Service Bus delivery count increments.
        return await _pipeline.ExecuteAsync(async ct =>
        {
            var downloadResponse = await blobClient.DownloadStreamingAsync(cancellationToken: ct);
            using var streamingResult = downloadResponse.Value;
            var ms = new MemoryStream();
            await streamingResult.Content.CopyToAsync(ms, ct);
            ms.Position = 0;
            return (Stream)ms;
        }, cancellationToken);
    }

    // BlobUriBuilder handles both Azurite path-style (host is an IP, "localhost", or has no dot —
    // e.g. the "azurite" Docker service name) and cloud host-style
    // (https://{account}.blob.core.windows.net/{container}/{blob}) URIs, so no scheme/segment
    // hand-parsing is needed here.
    internal static (string ContainerName, string BlobName) ParseStoragePath(Uri storagePath)
    {
        var builder = new BlobUriBuilder(storagePath);
        return (builder.BlobContainerName, builder.BlobName);
    }
}
