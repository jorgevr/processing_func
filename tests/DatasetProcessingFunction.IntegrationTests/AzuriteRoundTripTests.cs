using Azure.Storage.Blobs;
using DatasetProcessingFunction.Domain.Models;
using DatasetProcessingFunction.Domain.ValueObjects;
using DatasetProcessingFunction.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.Registry;

namespace DatasetProcessingFunction.IntegrationTests;

/// <summary>
/// T6 (docs/contract-migration.md R3.6; AGENTS.md §6 "Runtime behaviour is proven on the compose
/// stack ... Mocked integration tests do not count as proof"): a real round trip against a live
/// Azurite — <see cref="BronzeWriter.WriteAsync"/> writes, the returned URI is handed straight to
/// <see cref="AdlsDatasetReader.ReadAsync"/> (which parses it via <c>ParseStoragePath</c>
/// internally), and the bytes read back must equal the bytes an independent download — fetched via
/// the blob path the test itself expects, not derived from the returned URI — sees for the same
/// blob. Requires <see cref="IntegrationFactAttribute.AzuriteConnectionStringVariable"/>; each test
/// skips with an explicit reason when it is absent, rather than silently passing.
/// </summary>
public sealed class AzuriteRoundTripTests
{
    private const string Container = "bronze";

    private static BlobServiceClient BuildBlobServiceClient() => new(
        Environment.GetEnvironmentVariable(IntegrationFactAttribute.AzuriteConnectionStringVariable));

    private static VendorSchemaMapping BuildMapping() => new()
    {
        VendorId = "vendor-t6",
        SchemaVersion = "v1",
        Delimiter = ',',
        Encoding = "UTF-8",
        ColumnMappings = [new ColumnMapping("pwr_kw", "power_w", "decimal", "kw_to_w")],
        RequiredFields = []
    };

    private static CanonicalRecord MakeRecord() => new(
        SiteId: "site-t6",
        Timestamp: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
        IngestionTime: new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
        SourceDatasetId: "t6",
        SchemaVersion: "v1",
        Fields: new Dictionary<string, object> { ["power_w"] = 10.5m });

    [IntegrationFact(DisplayName = "Azurite round trip: plain path")]
    public Task WriteAsync_ThenReadAsync_PlainPath_ReturnsIdenticalBytes() =>
        AssertRoundTrip($"t6-plain-{Guid.NewGuid():N}");

    [IntegrationFact(DisplayName = "Azurite round trip: '=' in the path (percent-encoded %3D)")]
    public Task WriteAsync_ThenReadAsync_EqualsSignInPath_ReturnsIdenticalBytes() =>
        AssertRoundTrip($"t6=eq={Guid.NewGuid():N}");

    [IntegrationFact(DisplayName = "Azurite round trip: a space in the path (percent-encoded %20)")]
    public Task WriteAsync_ThenReadAsync_SpaceInPath_ReturnsIdenticalBytes() =>
        AssertRoundTrip($"t6 space {Guid.NewGuid():N}");

    private static async Task AssertRoundTrip(string datasetIdValue)
    {
        var serviceClient = BuildBlobServiceClient();
        await serviceClient.GetBlobContainerClient(Container).CreateIfNotExistsAsync();

        var pipelineProvider = new ResiliencePipelineRegistry<string>();
        pipelineProvider.GetOrAddPipeline("adls-read", (builder, _) => { });

        var writer = new BronzeWriter(serviceClient, Container, NullLogger<BronzeWriter>.Instance);
        var reader = new AdlsDatasetReader(serviceClient, pipelineProvider, NullLogger<AdlsDatasetReader>.Instance);

        var datasetId = new DatasetId(datasetIdValue);
        var date = new DateOnly(2026, 10, 1);

        var knownBlobPath = $"{datasetIdValue}/{date:yyyy-MM-dd}/data.parquet";
        try
        {
            var writtenUri = await writer.WriteAsync(
                datasetId, date, [MakeRecord()], BuildMapping(), CancellationToken.None);

            // Independent ground truth: fetched via the blob path the test itself expects
            // WriteAsync to have used (computed from datasetIdValue/date, not from writtenUri),
            // authenticated the same way as the writer/reader — bypassing BOTH BronzeWriter's and
            // AdlsDatasetReader's own URI/path resolution.
            var independentDownload = await serviceClient.GetBlobContainerClient(Container)
                .GetBlobClient(knownBlobPath)
                .DownloadContentAsync();
            var expectedBytes = independentDownload.Value.Content.ToArray();

            await using var readStream = await reader.ReadAsync(writtenUri, CancellationToken.None);
            using var actual = new MemoryStream();
            await readStream.CopyToAsync(actual);
            var actualBytes = actual.ToArray();

            actualBytes.Should().Equal(expectedBytes,
                "ReadAsync must return exactly the bytes WriteAsync persisted at its own returned URI");
            actualBytes.Should().NotBeEmpty("a real Parquet payload was written");
        }
        finally
        {
            await serviceClient.GetBlobContainerClient(Container).DeleteBlobIfExistsAsync(knownBlobPath);
        }
    }
}
