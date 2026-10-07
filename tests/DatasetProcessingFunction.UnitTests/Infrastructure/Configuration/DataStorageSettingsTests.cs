using DatasetProcessingFunction.Infrastructure.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace DatasetProcessingFunction.UnitTests.Infrastructure.Configuration;

/// <summary>
/// T1 (docs/contract-migration.md R3.6; docs/contracts.md "Shared configuration" §5.1):
/// <see cref="DataStorageSettings.Resolve"/> is a pure function of <see cref="IConfiguration"/> —
/// no DI, no network. Written against the published contract (variable names, precedence, defaults),
/// not against the current implementation's internals.
/// </summary>
public sealed class DataStorageSettingsTests
{
    private static IConfiguration BuildConfig(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Resolve_LocalOnly_ResolvesConnectionStringMode()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DATA_STORAGE_CONNECTION"] = "UseDevelopmentStorage=true",
        });

        var settings = DataStorageSettings.Resolve(config);

        // CI-3 PROOF (throwaway, to be reverted): deliberately wrong assertion
        settings.Mode.Should().Be(StorageMode.Cloud);
        settings.ConnectionString.Should().Be("UseDevelopmentStorage=true");
        settings.AccountUrl.Should().BeNull();
    }

    [Fact]
    public void Resolve_CloudOnly_ResolvesAccountUrlMode()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DATA_STORAGE_ACCOUNT_URL"] = "https://anomaliadata.blob.core.windows.net",
        });

        var settings = DataStorageSettings.Resolve(config);

        settings.Mode.Should().Be(StorageMode.Cloud);
        settings.AccountUrl.Should().Be("https://anomaliadata.blob.core.windows.net");
        settings.ConnectionString.Should().BeNull();
    }

    [Fact]
    public void Resolve_BothSet_ConnectionStringWins()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DATA_STORAGE_CONNECTION"] = "UseDevelopmentStorage=true",
            ["DATA_STORAGE_ACCOUNT_URL"] = "https://anomaliadata.blob.core.windows.net",
        });

        var settings = DataStorageSettings.Resolve(config);

        settings.Mode.Should().Be(StorageMode.Local);
        settings.ConnectionString.Should().Be("UseDevelopmentStorage=true");
        settings.AccountUrl.Should().BeNull(
            "the account URL must not be captured once the connection string has won");
    }

    [Fact]
    public void Resolve_NeitherSet_ThrowsNamingBothVariables()
    {
        var config = BuildConfig(new Dictionary<string, string?>());

        var act = () => DataStorageSettings.Resolve(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*DATA_STORAGE_CONNECTION*")
            .Which.Message.Should().Contain("DATA_STORAGE_ACCOUNT_URL");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_EmptyOrWhitespaceConnectionString_CountsAsUnset_FallsBackToAccountUrl(string blank)
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DATA_STORAGE_CONNECTION"] = blank,
            ["DATA_STORAGE_ACCOUNT_URL"] = "https://anomaliadata.blob.core.windows.net",
        });

        var settings = DataStorageSettings.Resolve(config);

        settings.Mode.Should().Be(StorageMode.Cloud);
        settings.AccountUrl.Should().Be("https://anomaliadata.blob.core.windows.net");
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData("", "   ")]
    public void Resolve_EmptyOrWhitespaceBothVariables_ThrowsAsIfNeitherSet(string connBlank, string urlBlank)
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DATA_STORAGE_CONNECTION"] = connBlank,
            ["DATA_STORAGE_ACCOUNT_URL"] = urlBlank,
        });

        var act = () => DataStorageSettings.Resolve(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*DATA_STORAGE_CONNECTION*")
            .Which.Message.Should().Contain("DATA_STORAGE_ACCOUNT_URL");
    }

    [Fact]
    public void Resolve_NoContainerVariablesSet_UsesDefaults()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DATA_STORAGE_CONNECTION"] = "UseDevelopmentStorage=true",
        });

        var settings = DataStorageSettings.Resolve(config);

        settings.BronzeContainer.Should().Be("bronze");
        settings.SilverContainer.Should().Be("silver");
        settings.QuarantineContainer.Should().Be("quarantine");
    }

    [Fact]
    public void Resolve_ContainerVariablesSet_OverrideDefaults()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DATA_STORAGE_CONNECTION"] = "UseDevelopmentStorage=true",
            ["BRONZE_CONTAINER"] = "custom-bronze",
            ["SILVER_CONTAINER"] = "custom-silver",
            ["QUARANTINE_CONTAINER"] = "custom-quarantine",
        });

        var settings = DataStorageSettings.Resolve(config);

        settings.BronzeContainer.Should().Be("custom-bronze");
        settings.SilverContainer.Should().Be("custom-silver");
        settings.QuarantineContainer.Should().Be("custom-quarantine");
    }

    [Theory]
    [InlineData("BRONZE_CONTAINER", "")]
    [InlineData("SILVER_CONTAINER", "   ")]
    public void Resolve_EmptyOrWhitespaceContainerVariable_CountsAsUnset_UsesDefault(string key, string blank)
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DATA_STORAGE_CONNECTION"] = "UseDevelopmentStorage=true",
            [key] = blank,
        });

        var settings = DataStorageSettings.Resolve(config);

        settings.BronzeContainer.Should().Be("bronze");
        settings.SilverContainer.Should().Be("silver");
    }

    [Fact]
    public void Resolve_UnknownExtraVariables_AreIgnored()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["DATA_STORAGE_CONNECTION"] = "UseDevelopmentStorage=true",
            ["BRONZE_CONTAINER"] = "custom-bronze",
            ["SOME_UNRELATED_VARIABLE"] = "should-be-ignored",
            ["ANOTHER_RANDOM_KEY"] = "also-ignored",
        });

        var settings = DataStorageSettings.Resolve(config);

        settings.Mode.Should().Be(StorageMode.Local);
        settings.ConnectionString.Should().Be("UseDevelopmentStorage=true");
        settings.BronzeContainer.Should().Be("custom-bronze");
        settings.SilverContainer.Should().Be("silver");
        settings.QuarantineContainer.Should().Be("quarantine");
    }
}
