using System.IO;
using Azure;
using Azure.Storage.Files.DataLake;
using DatasetProcessingFunction.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Registry;

namespace DatasetProcessingFunction.Infrastructure.Storage;

public sealed class AdlsDatasetReader : IDatasetReader
{
    private readonly DataLakeServiceClient _serviceClient;
    private readonly ResiliencePipeline _pipeline;
    private readonly ILogger<AdlsDatasetReader> _logger;

    public AdlsDatasetReader(
        DataLakeServiceClient serviceClient,
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

        var (fileSystemName, filePath) = ParseAdlsUri(storagePath);
        var fileClient = _serviceClient
            .GetFileSystemClient(fileSystemName)
            .GetFileClient(filePath);

        // Polly handles transient retries (IOException, RequestFailedException 429/500/503/408).
        // Cancellation during backoff wait propagates immediately without consuming a retry attempt.
        // After retry exhaustion Polly re-throws the last exception — Service Bus delivery count increments.
        return await _pipeline.ExecuteAsync(async ct =>
        {
            var downloadResponse = await fileClient.ReadAsync(ct);
            var ms = new MemoryStream();
            await downloadResponse.Value.Content.CopyToAsync(ms, ct);
            ms.Position = 0;
            return (Stream)ms;
        }, cancellationToken);
    }

    private static (string FileSystem, string FilePath) ParseAdlsUri(Uri uri)
    {
        // abfss://filesystem@account.dfs.core.windows.net/path/to/file.csv
        if (uri.Scheme is "abfss" or "abfs")
        {
            var fileSystem = uri.UserInfo;
            var filePath = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
            return (fileSystem, filePath);
        }

        // HTTP (Azurite): /devstoreaccount1/container/path
        // Use UnescapeDataString so spaces (encoded as %20 by the Uri constructor) are decoded
        // back to their original form before being passed to the DataLake SDK, which re-encodes
        // the path itself — passing pre-encoded strings would cause double-encoding.
        var segments = Uri.UnescapeDataString(uri.AbsolutePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            throw new ArgumentException($"Cannot parse ADLS URI: {uri}", nameof(uri));

        var container = segments[segments.Length > 2 ? 1 : 0];
        var path = string.Join('/', segments.Skip(segments.Length > 2 ? 2 : 1));
        return (container, path);
    }
}
