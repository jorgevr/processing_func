using Azure.Storage.Files.DataLake;
using DatasetProcessingFunction.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace DatasetProcessingFunction.Infrastructure.Storage;

public sealed class AdlsDatasetReader : IDatasetReader
{
    private readonly DataLakeServiceClient _serviceClient;
    private readonly ILogger<AdlsDatasetReader> _logger;

    public AdlsDatasetReader(DataLakeServiceClient serviceClient, ILogger<AdlsDatasetReader> logger)
    {
        _serviceClient = serviceClient ?? throw new ArgumentNullException(nameof(serviceClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Stream> ReadAsync(Uri storagePath, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Reading dataset from {StoragePath}", storagePath);

        // Parse abfss://container@account.dfs.core.windows.net/path
        var (fileSystemName, filePath) = ParseAdlsUri(storagePath);

        var fileSystemClient = _serviceClient.GetFileSystemClient(fileSystemName);
        var fileClient = fileSystemClient.GetFileClient(filePath);

        var downloadResponse = await fileClient.ReadAsync(cancellationToken);
        var memoryStream = new MemoryStream();
        await downloadResponse.Value.Content.CopyToAsync(memoryStream, cancellationToken);
        memoryStream.Position = 0;
        return memoryStream;
    }

    private static (string FileSystem, string FilePath) ParseAdlsUri(Uri uri)
    {
        // abfss://filesystem@account.dfs.core.windows.net/path/to/file.csv
        // or http://127.0.0.1:10000/devstoreaccount1/container/path/file.csv (local emulator)
        if (uri.Scheme is "abfss" or "abfs")
        {
            var fileSystem = uri.UserInfo;
            var filePath = uri.AbsolutePath.TrimStart('/');
            return (fileSystem, filePath);
        }

        // HTTP (Azurite): /devstoreaccount1/container/path
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            throw new ArgumentException($"Cannot parse ADLS URI: {uri}", nameof(uri));

        var container = segments[segments.Length > 2 ? 1 : 0];
        var path = string.Join('/', segments.Skip(segments.Length > 2 ? 2 : 1));
        return (container, path);
    }
}
