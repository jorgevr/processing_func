using Azure.Storage.Blobs;
using DatasetProcessingFunction.Infrastructure.Configuration;
using DatasetProcessingFunction.Infrastructure.Storage;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DatasetProcessingFunction.UnitTests.Infrastructure.Configuration;

/// <summary>
/// T2 (docs/contracts.md "Shared configuration" §5.2): <see cref="SchemaRegistrySettings.Resolve"/>
/// follows the same precedence contract as the data-storage pair, and the schema-registry client
/// must be a genuinely separate <see cref="BlobServiceClient"/> from the data-storage client — never
/// an alias, even when both resolve successfully.
/// </summary>
public sealed class SchemaRegistrySettingsTests
{
    private static IConfiguration BuildConfig(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Resolve_ConnectionOnly_ResolvesConnectionStringMode()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["SCHEMA_REGISTRY_BLOB_CONNECTION"] = "UseDevelopmentStorage=true",
        });

        var settings = SchemaRegistrySettings.Resolve(config);

        settings.Mode.Should().Be(StorageMode.Local);
        settings.ConnectionString.Should().Be("UseDevelopmentStorage=true");
        settings.AccountName.Should().BeNull();
    }

    [Fact]
    public void Resolve_AccountOnly_ResolvesAccountMode()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["SCHEMA_REGISTRY_ACCOUNT"] = "anomaliadata",
        });

        var settings = SchemaRegistrySettings.Resolve(config);

        settings.Mode.Should().Be(StorageMode.Cloud);
        settings.AccountName.Should().Be("anomaliadata");
        settings.ConnectionString.Should().BeNull();
    }

    [Fact]
    public void Resolve_BothSet_ConnectionStringWins()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["SCHEMA_REGISTRY_BLOB_CONNECTION"] = "UseDevelopmentStorage=true",
            ["SCHEMA_REGISTRY_ACCOUNT"] = "anomaliadata",
        });

        var settings = SchemaRegistrySettings.Resolve(config);

        settings.Mode.Should().Be(StorageMode.Local);
        settings.ConnectionString.Should().Be("UseDevelopmentStorage=true");
        settings.AccountName.Should().BeNull();
    }

    [Fact]
    public void Resolve_NeitherSet_ThrowsNamingBothVariables()
    {
        var config = BuildConfig(new Dictionary<string, string?>());

        var act = () => SchemaRegistrySettings.Resolve(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SCHEMA_REGISTRY_BLOB_CONNECTION*")
            .Which.Message.Should().Contain("SCHEMA_REGISTRY_ACCOUNT");
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    public void Resolve_EmptyOrWhitespaceBothVariables_ThrowsAsIfNeitherSet(string connBlank, string accountBlank)
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["SCHEMA_REGISTRY_BLOB_CONNECTION"] = connBlank,
            ["SCHEMA_REGISTRY_ACCOUNT"] = accountBlank,
        });

        var act = () => SchemaRegistrySettings.Resolve(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SCHEMA_REGISTRY_BLOB_CONNECTION*")
            .Which.Message.Should().Contain("SCHEMA_REGISTRY_ACCOUNT");
    }

    [Fact]
    public void Resolve_NoContainerVariableSet_UsesDefault()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["SCHEMA_REGISTRY_BLOB_CONNECTION"] = "UseDevelopmentStorage=true",
        });

        var settings = SchemaRegistrySettings.Resolve(config);

        settings.Container.Should().Be("schema-registry");
    }

    [Fact]
    public void Resolve_ContainerVariableSet_OverridesDefault()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["SCHEMA_REGISTRY_BLOB_CONNECTION"] = "UseDevelopmentStorage=true",
            ["SCHEMA_REGISTRY_CONTAINER"] = "custom-schema-registry",
        });

        var settings = SchemaRegistrySettings.Resolve(config);

        settings.Container.Should().Be("custom-schema-registry");
    }

    [Fact]
    public void Resolve_UnknownExtraVariables_AreIgnored()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["SCHEMA_REGISTRY_BLOB_CONNECTION"] = "UseDevelopmentStorage=true",
            ["SOME_UNRELATED_VARIABLE"] = "should-be-ignored",
        });

        var settings = SchemaRegistrySettings.Resolve(config);

        settings.Mode.Should().Be(StorageMode.Local);
        settings.ConnectionString.Should().Be("UseDevelopmentStorage=true");
    }

    [Fact]
    public void RegisteredClient_DoesNotResolveToTheDataStorageAccount_WhenConfiguredDifferently()
    {
        // Two different, syntactically valid connection strings pointing at different endpoints —
        // if AddSchemaRegistry ever aliased the data-storage client (or vice versa), both resolved
        // BlobServiceClients would report the same Uri.
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DATA_STORAGE_CONNECTION"] = "UseDevelopmentStorage=true",
            ["SCHEMA_REGISTRY_BLOB_CONNECTION"] =
                "DefaultEndpointsProtocol=http;AccountName=otheraccount;" +
                "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
                "BlobEndpoint=http://127.0.0.1:20000/otheraccount",
        });

        var services = new ServiceCollection();
        services.AddDataStorage(config);
        services.AddSchemaRegistry(config);
        using var provider = services.BuildServiceProvider();

        var dataStorageClient = provider.GetRequiredKeyedService<BlobServiceClient>(DataStorageKeys.DataStorage);
        var schemaRegistryClient = provider.GetRequiredService<BlobServiceClient>();

        dataStorageClient.Uri.Should().NotBe(schemaRegistryClient.Uri,
            "the schema-registry client must be built from its own configuration, " +
            "never the data-storage client's");
        dataStorageClient.Uri.Host.Should().Be("127.0.0.1");
        dataStorageClient.Uri.Port.Should().Be(10000);
        schemaRegistryClient.Uri.Port.Should().Be(20000);
    }
}
