using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DatasetProcessingFunction.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Polly.Registry;

namespace DatasetProcessingFunction.UnitTests.Infrastructure;

public sealed class AdlsDatasetReaderTests
{
    // Inline literal cases — no filesystem/workspace dependency, so these always run regardless
    // of checkout layout (this service's own isolated CI checkout has no sibling contracts/
    // folder; the cross-repo contract-example drift check is the workspace root's CI, ADR 0004
    // rule 4 — not this one).
    public static TheoryData<string, string, string> ValidDatasetAvailableStoragePaths => new()
    {
        // contracts/examples/dataset-available.v1/valid-minimal-local.json — local Azurite,
        // IP-style host, unencoded '='.
        {
            "http://127.0.0.1:10000/devstoreaccount1/bronze/source=pvdaq/dataset=9068_ac_power/ingestion_date=2026-09-24/9068_ac_power_v1.csv",
            "bronze",
            "source=pvdaq/dataset=9068_ac_power/ingestion_date=2026-09-24/9068_ac_power_v1.csv"
        },
        // contracts/examples/dataset-available.v1/valid-rerun-cloud-with-additive-device-id.json
        // — cloud host-style, unencoded '='.
        {
            "https://anomaliadata.blob.core.windows.net/bronze/source=pvdaq/dataset=9068_ac_power/ingestion_date=2026-09-25/9068_ac_power_v3.csv",
            "bronze",
            "source=pvdaq/dataset=9068_ac_power/ingestion_date=2026-09-25/9068_ac_power_v3.csv"
        },
        // The real path BlobClient.Uri emits in the docker-compose stack (ingestion-func R2.1,
        // verified live against Azurite): percent-encoded '=' (BlobUriBuilder decodes it back),
        // docker-network "azurite" hostname.
        {
            "http://azurite:10000/devstoreaccount1/bronze/source%3Dpvdaq/dataset%3D9068_ac_power/ingestion_date%3D2026-09-28/9068_ac_power_v1.csv",
            "bronze",
            "source=pvdaq/dataset=9068_ac_power/ingestion_date=2026-09-28/9068_ac_power_v1.csv"
        },
    };

    [Theory]
    [MemberData(nameof(ValidDatasetAvailableStoragePaths))]
    public void ParseStoragePath_ExtractsContainerAndBlobName_ForEveryValidExample(
        string storagePathText, string expectedContainer, string expectedBlobName)
    {
        var (containerName, blobName) = AdlsDatasetReader.ParseStoragePath(new Uri(storagePathText));

        containerName.Should().Be(expectedContainer, storagePathText);
        blobName.Should().Be(expectedBlobName, storagePathText);
    }

    [Fact]
    public void ParseStoragePath_LocalAzuritePathStyle_ExtractsContainerAndBlobName()
    {
        var uri = new Uri("http://127.0.0.1:10000/devstoreaccount1/bronze/source=pvdaq/9068_v1.csv");

        var (containerName, blobName) = AdlsDatasetReader.ParseStoragePath(uri);

        containerName.Should().Be("bronze");
        blobName.Should().Be("source=pvdaq/9068_v1.csv");
    }

    [Fact]
    public void ParseStoragePath_DockerNetworkAzuriteHostname_ExtractsContainerAndBlobName()
    {
        // No dot in "azurite" — path-style, same as the IP form Azurite is reached by locally
        // outside Docker (docker-compose reaches Azurite via the service name, not an IP).
        var uri = new Uri("http://azurite:10000/devstoreaccount1/bronze/source=pvdaq/9068_v1.csv");

        var (containerName, blobName) = AdlsDatasetReader.ParseStoragePath(uri);

        containerName.Should().Be("bronze");
        blobName.Should().Be("source=pvdaq/9068_v1.csv");
    }

    [Fact]
    public void ParseStoragePath_CloudHostStyle_ExtractsContainerAndBlobName()
    {
        var uri = new Uri("https://anomaliadata.blob.core.windows.net/bronze/source=pvdaq/9068_v1.csv");

        var (containerName, blobName) = AdlsDatasetReader.ParseStoragePath(uri);

        containerName.Should().Be("bronze");
        blobName.Should().Be("source=pvdaq/9068_v1.csv");
    }

    [Fact]
    public void ParseStoragePath_PathWithSpace_DecodesBlobName()
    {
        var uri = new Uri("http://127.0.0.1:10000/devstoreaccount1/bronze/dataset%20name/file.csv");

        var (_, blobName) = AdlsDatasetReader.ParseStoragePath(uri);

        blobName.Should().Be("dataset name/file.csv");
    }

    /// <summary>
    /// A <see cref="Stream"/> that records whether <see cref="Dispose(bool)"/> ran, so a test can
    /// assert disposal without depending on any internal detail of what disposes it.
    /// </summary>
    private sealed class DisposeTrackingStream : MemoryStream
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// T5 (docs/contract-migration.md R3.6): <see cref="AdlsDatasetReader.ReadAsync"/> must dispose
    /// the SDK's <see cref="BlobDownloadStreamingResult"/> after copying its content — disposing it
    /// disposes the underlying <see cref="BlobDownloadStreamingResult.Content"/> stream (per the
    /// Azure SDK's own documented behavior), which this test observes directly on a tracking stream
    /// rather than asserting on a mock of the call being tested.
    /// </summary>
    [Fact]
    public async Task ReadAsync_DisposesTheDownloadStreamingResult()
    {
        var bytes = "site_id,timestamp\n1,2026-01-01\n"u8.ToArray();
        var trackingStream = new DisposeTrackingStream();
        trackingStream.Write(bytes);
        trackingStream.Position = 0;

        var downloadResult = BlobsModelFactory.BlobDownloadStreamingResult(
            trackingStream, BlobsModelFactory.BlobDownloadDetails());

        var blobClientMock = new Mock<BlobClient>();
        blobClientMock
            .Setup(c => c.DownloadStreamingAsync(It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(downloadResult, Mock.Of<Response>()));

        var containerClientMock = new Mock<BlobContainerClient>();
        containerClientMock.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blobClientMock.Object);

        var serviceClientMock = new Mock<BlobServiceClient>();
        serviceClientMock.Setup(s => s.GetBlobContainerClient(It.IsAny<string>())).Returns(containerClientMock.Object);

        var pipelineProvider = new ResiliencePipelineRegistry<string>();
        pipelineProvider.GetOrAddPipeline("adls-read", (builder, _) => { });

        var reader = new AdlsDatasetReader(
            serviceClientMock.Object, pipelineProvider, NullLogger<AdlsDatasetReader>.Instance);

        trackingStream.IsDisposed.Should().BeFalse("not yet read");

        using var resultStream = await reader.ReadAsync(
            new Uri("http://127.0.0.1:10000/devstoreaccount1/bronze/t5-dataset/file.csv"),
            CancellationToken.None);

        trackingStream.IsDisposed.Should().BeTrue(
            "ReadAsync must dispose the BlobDownloadStreamingResult once its content has been copied");

        var buffer = new byte[bytes.Length];
        resultStream.Position = 0;
        await resultStream.ReadExactlyAsync(buffer);
        buffer.Should().Equal(bytes, "the copied stream returned to the caller must still be readable");
    }
}
