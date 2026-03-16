using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Storage.Blobs;
using DatasetProcessingFunction.Application.Interfaces;
using DatasetProcessingFunction.Domain.Models;
using Microsoft.Extensions.Logging;

namespace DatasetProcessingFunction.Infrastructure.SchemaRegistry;

public sealed class BlobSchemaRegistry : ISchemaRegistry
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly string _containerName;
    private readonly ILogger<BlobSchemaRegistry> _logger;
    private readonly ConcurrentDictionary<string, VendorSchemaMapping> _cache = new();

    public BlobSchemaRegistry(
        BlobServiceClient blobServiceClient,
        string containerName,
        ILogger<BlobSchemaRegistry> logger)
    {
        _blobServiceClient = blobServiceClient ?? throw new ArgumentNullException(nameof(blobServiceClient));
        _containerName = containerName ?? throw new ArgumentNullException(nameof(containerName));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<VendorSchemaMapping?> GetAsync(
        string vendorId,
        string schemaVersion,
        CancellationToken cancellationToken = default)
    {
        var key = $"{vendorId}:{schemaVersion}";

        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var blobName = $"{vendorId}-{schemaVersion}.json";
        _logger.LogDebug("Loading schema mapping from blob: {BlobName}", blobName);

        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobClient = containerClient.GetBlobClient(blobName);

        if (!await blobClient.ExistsAsync(cancellationToken))
        {
            _logger.LogWarning("Schema mapping not found: {BlobName}", blobName);
            return null;
        }

        var response = await blobClient.DownloadContentAsync(cancellationToken);
        var mapping = JsonSerializer.Deserialize<VendorSchemaMapping>(
            response.Value.Content,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (mapping is not null)
            _cache.TryAdd(key, mapping);

        return mapping;
    }
}
