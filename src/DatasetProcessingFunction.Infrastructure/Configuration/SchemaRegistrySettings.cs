using Microsoft.Extensions.Configuration;

namespace DatasetProcessingFunction.Infrastructure.Configuration;

/// <summary>
/// Resolved schema-registry configuration — a separate storage account from
/// <see cref="DataStorageSettings"/> (docs/contracts.md "Shared configuration" §5.2). A pure,
/// DI-free value produced by <see cref="Resolve"/> — constructible and testable with a plain
/// <see cref="IConfiguration"/>, no hosting or DI container required.
/// </summary>
public sealed class SchemaRegistrySettings
{
    public StorageMode Mode { get; set; }

    /// <summary>Set only when <see cref="Mode"/> is <see cref="StorageMode.Local"/>.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// The storage account *name* (not a full URL) — set only when <see cref="Mode"/> is
    /// <see cref="StorageMode.Cloud"/>. The account URL is built as
    /// <c>https://{AccountName}.blob.core.windows.net</c>.
    /// </summary>
    public string? AccountName { get; set; }

    public string Container { get; set; } = "schema-registry";

    /// <summary>
    /// Resolves schema-registry settings from configuration.
    /// <c>SCHEMA_REGISTRY_BLOB_CONNECTION</c> wins if set (even when
    /// <c>SCHEMA_REGISTRY_ACCOUNT</c> is also present); otherwise <c>SCHEMA_REGISTRY_ACCOUNT</c>
    /// is used. A value that is empty or all-whitespace counts as unset. If neither is set,
    /// throws naming both variables.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Neither <c>SCHEMA_REGISTRY_BLOB_CONNECTION</c> nor <c>SCHEMA_REGISTRY_ACCOUNT</c> is set.
    /// </exception>
    public static SchemaRegistrySettings Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var container = configuration["SCHEMA_REGISTRY_CONTAINER"];
        var settings = new SchemaRegistrySettings
        {
            Container = string.IsNullOrWhiteSpace(container) ? "schema-registry" : container,
        };

        var connectionString = configuration["SCHEMA_REGISTRY_BLOB_CONNECTION"];
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            settings.Mode = StorageMode.Local;
            settings.ConnectionString = connectionString;
            return settings;
        }

        var accountName = configuration["SCHEMA_REGISTRY_ACCOUNT"];
        if (!string.IsNullOrWhiteSpace(accountName))
        {
            settings.Mode = StorageMode.Cloud;
            settings.AccountName = accountName;
            return settings;
        }

        throw new InvalidOperationException(
            "Either SCHEMA_REGISTRY_BLOB_CONNECTION or SCHEMA_REGISTRY_ACCOUNT is required.");
    }
}
