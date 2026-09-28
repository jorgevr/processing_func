using System.Text.Json;
using DatasetProcessingFunction.Infrastructure.Storage;
using FluentAssertions;

namespace DatasetProcessingFunction.UnitTests.Infrastructure;

public sealed class AdlsDatasetReaderTests
{
    // Reads the workspace root's contracts/examples/ (this repo has no vendored copy until R3.7).
    // That directory exists only when this repo is checked out inside the anomalia-platform
    // workspace (as it is here) — this service's own isolated CI checkout (.github/workflows/
    // ci-cd.yml) does not have a sibling contracts/ folder, so this yields no cases there rather
    // than failing the build; the cross-repo drift/example check is the workspace root's CI
    // (ADR 0004 rule 4), not this one.
    public static IEnumerable<object[]> ValidDatasetAvailableExamples()
    {
        var dir = FindContractExamplesDir();
        if (dir is null)
            yield break;

        foreach (var file in Directory.GetFiles(dir, "valid-*.json"))
            yield return [file];
    }

    [Theory]
    [MemberData(nameof(ValidDatasetAvailableExamples))]
    public void ParseStoragePath_ExtractsContainerAndBlobName_ForEveryValidExample(string exampleFile)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(exampleFile));
        var storagePathText = doc.RootElement.GetProperty("data").GetProperty("storage_path").GetString()!;

        // Independent expectation, not derived from BlobUriBuilder: every dataset-available.v1
        // example points at the "bronze" container, so the blob name is whatever follows
        // "/bronze/" in the raw URL string.
        const string marker = "/bronze/";
        var markerIndex = storagePathText.IndexOf(marker, StringComparison.Ordinal);
        markerIndex.Should().BeGreaterThanOrEqualTo(0,
            $"{exampleFile} is expected to point at the 'bronze' container");
        var expectedBlobName = Uri.UnescapeDataString(storagePathText[(markerIndex + marker.Length)..]);

        var (containerName, blobName) = AdlsDatasetReader.ParseStoragePath(new Uri(storagePathText));

        containerName.Should().Be("bronze", exampleFile);
        blobName.Should().Be(expectedBlobName, exampleFile);
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

    private static string? FindContractExamplesDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "contracts", "examples", "dataset-available.v1");
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
