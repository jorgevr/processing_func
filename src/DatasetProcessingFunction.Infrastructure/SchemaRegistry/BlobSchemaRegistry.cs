using System.Collections.Concurrent;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using DatasetProcessingFunction.Application.Interfaces;
using DatasetProcessingFunction.Domain.Exceptions;
using DatasetProcessingFunction.Domain.Models;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Registry;

namespace DatasetProcessingFunction.Infrastructure.SchemaRegistry;

public sealed class BlobSchemaRegistry : ISchemaRegistry
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly string _containerName;
    private readonly ResiliencePipeline _pipeline;
    private readonly ILogger<BlobSchemaRegistry> _logger;
    private readonly ConcurrentDictionary<string, VendorSchemaMapping> _cache = new();

    public BlobSchemaRegistry(
        BlobServiceClient blobServiceClient,
        string containerName,
        ResiliencePipelineProvider<string> pipelineProvider,
        ILogger<BlobSchemaRegistry> logger)
    {
        _blobServiceClient = blobServiceClient ?? throw new ArgumentNullException(nameof(blobServiceClient));
        _containerName = containerName ?? throw new ArgumentNullException(nameof(containerName));
        _pipeline = pipelineProvider.GetPipeline("schema-registry-read");
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

        var mapping = await _pipeline.ExecuteAsync(async ct =>
        {
            var blobName = $"{vendorId}-{schemaVersion}.json";
            _logger.LogDebug("Loading schema mapping from blob: {BlobName}", blobName);

            var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
            var blobClient = containerClient.GetBlobClient(blobName);

            try
            {
                var response = await blobClient.DownloadContentAsync(ct);
                return JsonSerializer.Deserialize<VendorSchemaMapping>(
                    response.Value.Content,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Non-retriable: mapping does not exist — dead-letter immediately (research.md §4)
                throw new UnknownSchemaException(vendorId, schemaVersion);
            }
        }, cancellationToken);

        if (mapping is null)
            throw new UnknownSchemaException(vendorId, schemaVersion);

        // FR-016a: schema must define a canonical mapping for 'timestamp'
        if (!mapping.ColumnMappings.Any(c =>
                string.Equals(c.CanonicalField, "timestamp", StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning(
                "Schema {VendorId}/{SchemaVersion} is missing canonical mapping for 'timestamp' — treating as unknown schema (FR-016a)",
                vendorId, schemaVersion);
            throw new UnknownSchemaException(vendorId, schemaVersion);
        }

        // FR-016: strict schemas must define a canonical mapping for 'site_id'.
        // Pass-through schemas are exempt because site_id is derived from the event payload context.
        if (!mapping.PassThroughUnmapped &&
            !mapping.ColumnMappings.Any(c =>
                string.Equals(c.CanonicalField, "site_id", StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning(
                "Schema {VendorId}/{SchemaVersion} is missing canonical mapping for 'site_id' — treating as unknown schema (FR-016)",
                vendorId, schemaVersion);
            throw new UnknownSchemaException(vendorId, schemaVersion);
        }

        // Validate column mapping bounds (fail fast on malformed schema)
        foreach (var col in mapping.ColumnMappings)
            col.EnsureValid();

        _cache.TryAdd(key, mapping);
        return mapping;
    }
}
