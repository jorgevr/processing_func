using System.Diagnostics.Tracing;
using Azure.Core.Diagnostics;
using DatasetProcessingFunction.Application.Interfaces;
using DatasetProcessingFunction.Domain.Models;
using DatasetProcessingFunction.Domain.ValueObjects;
using DatasetProcessingFunction.Infrastructure.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DatasetProcessingFunction.UnitTests.Infrastructure.Configuration;

/// <summary>
/// Declares the xUnit collection <see cref="ConsumerLevelStorageIsolationTests"/> runs in, with
/// parallelization disabled — see the remarks on that class for why.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AzureEventSourceCollection
{
    public const string Name = "AzureEventSource";
}

/// <summary>
/// Consumer-level isolation between the data-storage and schema-registry storage accounts
/// (docs/contracts.md "Shared configuration" §5.1/§5.2). The round-3 reviewer's surviving
/// mutation (<c>AddSchemaRegistry</c>'s <see cref="ISchemaRegistry"/> factory resolving the
/// data-storage keyed client) showed that comparing the two raw <c>BlobServiceClient</c>
/// registrations (<see cref="SchemaRegistrySettingsTests"/>) isn't enough — the bug is in which
/// client each CONSUMER (<see cref="ISchemaRegistry"/>, <see cref="IDatasetReader"/>,
/// <see cref="IBronzeWriter"/>) is actually wired to. These tests resolve the real consumers from
/// a real <see cref="IServiceProvider"/> built through <c>AddDataStorage</c>/<c>AddSchemaRegistry</c>
/// and observe which host each one actually talks to.
///
/// <para><b>Mechanism — no HttpPipelineTransport seam exists.</b> Both factories build
/// <c>BlobClientOptions</c> entirely inside themselves (<c>ServiceCollectionExtensions.cs</c>);
/// neither exposes a parameter or hook a caller can use to supply a custom
/// <c>BlobClientOptions.Transport</c>, so a literal recording-transport swap is not possible from
/// here without a source change — none was made (see the Test Author report for the smallest
/// proposed seam). Instead, both accounts are given hostnames under the <c>.invalid</c> TLD
/// (RFC 2606 — reserved, guaranteed to never resolve, unlike an arbitrary made-up label which a
/// stray wildcard DNS or split-horizon resolver could theoretically answer), so every call fails at
/// DNS resolution: no live Azurite, no real network, fully offline and deterministic (confirmed:
/// Azure Blob's resilience pipelines here have <c>Retry.MaxRetries = 0</c>, and a DNS failure's
/// <see cref="Azure.RequestFailedException.Status"/> doesn't match either pipeline's retryable set,
/// so exactly one attempt is made per call). <see cref="AzureEventSourceListener"/> — Azure.Core's
/// own public diagnostics listener — captures the pipeline's "Request" event, which is logged with
/// the full method+URI *before* the connection attempt, so the exact host and blob path are
/// observable even though the call never completes. This captures the same information a recording
/// transport would, without needing one.</para>
///
/// <para>Cloud mode (<c>DefaultAzureCredential</c>) cannot be exercised offline this way — these
/// tests stay in connection-string mode, matching every other local/emulator path in this suite.</para>
/// </summary>
/// <remarks>
/// <see cref="AzureEventSourceListener"/> hooks a process-wide EventSource: every Azure SDK HTTP
/// call anywhere in this test process is visible to whichever listener instance is active at the
/// time, regardless of which test or class made the call. xUnit normally runs different test
/// classes as separate collections in parallel, so a concurrently-running test in another class
/// that also issues a real (even if failing) Azure SDK HTTP call could have its request captured
/// by — or leak into — this class's listener. Pinning this class to its own
/// <see cref="AzureEventSourceCollection"/> with parallelization disabled removes that risk by
/// construction: nothing sharing this collection ever runs at the same time as these tests, now or
/// if more AzureEventSourceListener-based tests are added later.
/// </remarks>
[Collection(AzureEventSourceCollection.Name)]
public sealed class ConsumerLevelStorageIsolationTests
{
    private const string PublicAzuriteAccountKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private static string ConnectionStringFor(string host) =>
        "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
        $"AccountKey={PublicAzuriteAccountKey};BlobEndpoint=http://{host}:10000/devstoreaccount1";

    private static ServiceProvider BuildProvider(string dataStorageHost, string schemaRegistryHost)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DATA_STORAGE_CONNECTION"] = ConnectionStringFor(dataStorageHost),
            ["SCHEMA_REGISTRY_BLOB_CONNECTION"] = ConnectionStringFor(schemaRegistryHost),
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataStorage(config);
        services.AddSchemaRegistry(config);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Runs <paramref name="operation"/> while recording every Azure.Core "Request" diagnostic
    /// event (full method + URI, logged before the connection attempt). The operation is expected
    /// to fail — the configured host never resolves — so any exception is swallowed; only what was
    /// *attempted* matters here.
    /// </summary>
    private static async Task<List<string>> CaptureAttemptedRequestsAsync(Func<Task> operation)
    {
        var requests = new List<string>();
        using var listener = new AzureEventSourceListener((eventArgs, text) =>
        {
            if (eventArgs.EventName == "Request")
                requests.Add(text);
        }, EventLevel.Verbose);

        try
        {
            await operation();
        }
        catch
        {
            // Expected: the .invalid hostnames never resolve. Only the captured requests matter.
        }

        return requests;
    }

    private static VendorSchemaMapping EmptyMapping() => new()
    {
        VendorId = "vendor-consumer-isolation",
        SchemaVersion = "v1",
        Delimiter = ',',
        Encoding = "UTF-8",
        ColumnMappings = [],
        RequiredFields = []
    };

    public static TheoryData<string, string> AccountAssignments => new()
    {
        // T-A / T-B as specified.
        { "data-host.invalid", "registry-host.invalid" },
        // T-C: the SAME two hostnames, assignment swapped — not new literals — so a factory that
        // special-cases on the literal string "registry-host.invalid" (string-sniffing) rather than
        // on which configuration key it was actually given would still be caught.
        { "registry-host.invalid", "data-host.invalid" },
    };

    [Theory]
    [MemberData(nameof(AccountAssignments))]
    public async Task SchemaRegistry_ContactsOnlyItsOwnHost_NeverTheDataStorageHost(
        string dataStorageHost, string schemaRegistryHost)
    {
        using var provider = BuildProvider(dataStorageHost, schemaRegistryHost);
        var registry = provider.GetRequiredService<ISchemaRegistry>();

        var requests = await CaptureAttemptedRequestsAsync(
            () => registry.GetAsync("PVDAQ", "v1", CancellationToken.None));

        requests.Should().NotBeEmpty("the schema registry lookup must have attempted at least one HTTP request");
        requests.Should().OnlyContain(r => r.Contains(schemaRegistryHost, StringComparison.Ordinal),
            $"every request ISchemaRegistry issues must go to {schemaRegistryHost}, not {dataStorageHost}");
        requests.Should().Contain(r => r.Contains("PVDAQ-v1.json", StringComparison.Ordinal),
            "the registry must look up the blob named '{vendorId}-{schemaVersion}.json'");
        requests.Should().NotContain(r => r.Contains(dataStorageHost, StringComparison.Ordinal),
            "the schema registry must never contact the data-storage account");
    }

    [Theory]
    [MemberData(nameof(AccountAssignments))]
    public async Task DataStorageConsumers_ContactOnlyTheDataStorageHost_NeverTheSchemaRegistryHost(
        string dataStorageHost, string schemaRegistryHost)
    {
        using var provider = BuildProvider(dataStorageHost, schemaRegistryHost);
        var reader = provider.GetRequiredService<IDatasetReader>();
        var writer = provider.GetRequiredService<IBronzeWriter>();

        // The host actually contacted is governed by the data-storage BlobServiceClient's own
        // configured endpoint, not by this argument's authority — ParseStoragePath only reads the
        // container/blob name out of it.
        var readRequests = await CaptureAttemptedRequestsAsync(() => reader.ReadAsync(
            new Uri($"http://{dataStorageHost}:10000/devstoreaccount1/bronze/some/path.csv"),
            CancellationToken.None));

        readRequests.Should().NotBeEmpty("the dataset read must have attempted at least one HTTP request");
        readRequests.Should().OnlyContain(r => r.Contains(dataStorageHost, StringComparison.Ordinal));
        readRequests.Should().NotContain(r => r.Contains(schemaRegistryHost, StringComparison.Ordinal),
            "IDatasetReader must never contact the schema-registry account");

        var writeRequests = await CaptureAttemptedRequestsAsync(() => writer.WriteAsync(
            new DatasetId("consumer-isolation-dataset"),
            new DateOnly(2026, 10, 2),
            [],
            EmptyMapping(),
            CancellationToken.None));

        writeRequests.Should().NotBeEmpty("the Bronze write must have attempted at least one HTTP request");
        writeRequests.Should().OnlyContain(r => r.Contains(dataStorageHost, StringComparison.Ordinal));
        writeRequests.Should().NotContain(r => r.Contains(schemaRegistryHost, StringComparison.Ordinal),
            "IBronzeWriter must never contact the schema-registry account");
    }
}
