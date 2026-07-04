using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DatasetProcessingFunction.Domain.Exceptions;
using DatasetProcessingFunction.Domain.Models;
using DatasetProcessingFunction.Infrastructure.SchemaRegistry;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Polly.Registry;

namespace DatasetProcessingFunction.UnitTests.Infrastructure;

/// <summary>
/// Unit tests for BlobSchemaRegistry schema-level invariant validation (FR-016, FR-016a).
/// </summary>
public sealed class BlobSchemaRegistryTests
{
    private static string SerializeMapping(VendorSchemaMapping mapping) =>
        JsonSerializer.Serialize(mapping, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

    private static BlobSchemaRegistry BuildRegistry(string jsonContent)
    {
        var blobClientMock = new Mock<BlobClient>();
        var downloadResult = BlobsModelFactory.BlobDownloadResult(
            BinaryData.FromString(jsonContent));

        blobClientMock
            .Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(downloadResult, Mock.Of<Response>()));

        var containerClientMock = new Mock<BlobContainerClient>();
        containerClientMock
            .Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Returns(blobClientMock.Object);

        var blobServiceClientMock = new Mock<BlobServiceClient>();
        blobServiceClientMock
            .Setup(s => s.GetBlobContainerClient(It.IsAny<string>()))
            .Returns(containerClientMock.Object);

        // Register a no-op pipeline so the constructor's GetPipeline("schema-registry-read") succeeds
        var pipelineProvider = new ResiliencePipelineRegistry<string>();
        pipelineProvider.GetOrAddPipeline("schema-registry-read", (builder, _) => { });

        return new BlobSchemaRegistry(
            blobServiceClientMock.Object,
            "schema-registry",
            pipelineProvider,
            NullLogger<BlobSchemaRegistry>.Instance);
    }

    private static VendorSchemaMapping BuildValidMapping() => new()
    {
        VendorId = "vendor-abc",
        SchemaVersion = "v2",
        Delimiter = ',',
        Encoding = "UTF-8",
        ColumnMappings =
        [
            new ColumnMapping("ts", "timestamp", "datetime", null),
            new ColumnMapping("sid", "site_id", "string", null),
            new ColumnMapping("pwr_kw", "power_w", "decimal", "kw_to_w")
        ],
        RequiredFields = ["ts", "sid"]
    };

    [Fact]
    public async Task GetAsync_ValidSchema_ReturnsMappingWithBothRequiredCanonicalFields()
    {
        var registry = BuildRegistry(SerializeMapping(BuildValidMapping()));

        var result = await registry.GetAsync("vendor-abc", "v2", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ColumnMappings.Should().Contain(c =>
            c.CanonicalField.Equals("site_id", StringComparison.OrdinalIgnoreCase));
        result.ColumnMappings.Should().Contain(c =>
            c.CanonicalField.Equals("timestamp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetAsync_SchemaMissingSiteIdMapping_ThrowsUnknownSchemaException()
    {
        // FR-016: schema without site_id canonical mapping is treated as unknown
        var mapping = BuildValidMapping();
        mapping.ColumnMappings = mapping.ColumnMappings
            .Where(c => !c.CanonicalField.Equals("site_id", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var registry = BuildRegistry(SerializeMapping(mapping));

        var act = async () => await registry.GetAsync("vendor-abc", "v2", CancellationToken.None);

        await act.Should().ThrowAsync<UnknownSchemaException>()
            .Where(ex => ex.VendorId == "vendor-abc" && ex.SchemaVersion == "v2");
    }

    [Fact]
    public async Task GetAsync_SchemaMissingTimestampMapping_ThrowsUnknownSchemaException()
    {
        // FR-016a: schema without timestamp canonical mapping is treated as unknown
        var mapping = BuildValidMapping();
        mapping.ColumnMappings = mapping.ColumnMappings
            .Where(c => !c.CanonicalField.Equals("timestamp", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var registry = BuildRegistry(SerializeMapping(mapping));

        var act = async () => await registry.GetAsync("vendor-abc", "v2", CancellationToken.None);

        await act.Should().ThrowAsync<UnknownSchemaException>()
            .Where(ex => ex.VendorId == "vendor-abc" && ex.SchemaVersion == "v2");
    }
}
