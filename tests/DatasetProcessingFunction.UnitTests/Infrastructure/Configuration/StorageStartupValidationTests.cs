using DatasetProcessingFunction.Infrastructure.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DatasetProcessingFunction.UnitTests.Infrastructure.Configuration;

/// <summary>
/// T3 (AGENTS.md §6 "Definition of done" — "Configuration is validated at host startup, not
/// lazily on first invocation"): builds a real <see cref="IHost"/> wired the same way
/// <c>Program.cs</c> wires it (<see cref="ServiceCollectionExtensions.AddDataStorage"/> /
/// <see cref="ServiceCollectionExtensions.AddSchemaRegistry"/>) and asserts that
/// <see cref="IHost.StartAsync"/> itself throws on bad configuration — not that resolving a
/// service later throws. This is the fail-at-startup proof for both storage accounts.
/// </summary>
public sealed class StorageStartupValidationTests
{
    private static IConfiguration BuildConfig(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IHost BuildHost(IConfiguration configuration) =>
        new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddDataStorage(configuration);
                services.AddSchemaRegistry(configuration);
            })
            .Build();

    /// <summary>
    /// Walks the full exception chain (including <see cref="AggregateException"/>) so this test
    /// doesn't depend on exactly how the Generic Host wraps the validation failure.
    /// </summary>
    private static string CollectAllMessages(Exception ex)
    {
        var messages = new List<string>();
        void Visit(Exception? e)
        {
            if (e is null) return;
            messages.Add(e.Message);
            if (e is AggregateException agg)
                foreach (var inner in agg.InnerExceptions) Visit(inner);
            else
                Visit(e.InnerException);
        }
        Visit(ex);
        return string.Join(" | ", messages);
    }

    [Fact]
    public async Task StartAsync_MissingDataStorageConfig_ThrowsAtHostStart_NamingBothVariables()
    {
        using var host = BuildHost(BuildConfig(new Dictionary<string, string?>
        {
            // Schema registry is valid — isolates the failure to data storage.
            ["SCHEMA_REGISTRY_BLOB_CONNECTION"] = "UseDevelopmentStorage=true",
        }));

        var thrown = await Record.ExceptionAsync(() => host.StartAsync());

        thrown.Should().NotBeNull("a missing data-storage configuration must fail host start, not later");
        var allMessages = CollectAllMessages(thrown!);
        allMessages.Should().Contain("DATA_STORAGE_CONNECTION");
        allMessages.Should().Contain("DATA_STORAGE_ACCOUNT_URL");
    }

    [Fact]
    public async Task StartAsync_MissingSchemaRegistryConfig_ThrowsAtHostStart_NamingBothVariables()
    {
        using var host = BuildHost(BuildConfig(new Dictionary<string, string?>
        {
            // Data storage is valid — isolates the failure to the schema registry.
            ["DATA_STORAGE_CONNECTION"] = "UseDevelopmentStorage=true",
        }));

        var thrown = await Record.ExceptionAsync(() => host.StartAsync());

        thrown.Should().NotBeNull("a missing schema-registry configuration must fail host start, not later");
        var allMessages = CollectAllMessages(thrown!);
        allMessages.Should().Contain("SCHEMA_REGISTRY_BLOB_CONNECTION");
        allMessages.Should().Contain("SCHEMA_REGISTRY_ACCOUNT");
    }

    [Fact]
    public async Task StartAsync_MissingBothConfigs_ThrowsAtHostStart()
    {
        using var host = BuildHost(BuildConfig(new Dictionary<string, string?>()));

        var thrown = await Record.ExceptionAsync(() => host.StartAsync());

        thrown.Should().NotBeNull();
    }

    [Fact]
    public async Task StartAsync_ValidConfig_DoesNotThrow()
    {
        using var host = BuildHost(BuildConfig(new Dictionary<string, string?>
        {
            ["DATA_STORAGE_CONNECTION"] = "UseDevelopmentStorage=true",
            ["SCHEMA_REGISTRY_BLOB_CONNECTION"] = "UseDevelopmentStorage=true",
        }));

        var thrown = await Record.ExceptionAsync(() => host.StartAsync());

        thrown.Should().BeNull("valid local configuration must start cleanly");

        await host.StopAsync();
    }
}
