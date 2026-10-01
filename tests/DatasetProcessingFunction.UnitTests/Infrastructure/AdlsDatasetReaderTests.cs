using DatasetProcessingFunction.Infrastructure.Storage;
using FluentAssertions;

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
}
