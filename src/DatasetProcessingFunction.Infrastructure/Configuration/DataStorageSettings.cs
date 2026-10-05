using Microsoft.Extensions.Configuration;

namespace DatasetProcessingFunction.Infrastructure.Configuration;

/// <summary>
/// Resolved data-storage configuration (ADR 0005 — Blob API everywhere; docs/contracts.md
/// "Shared configuration" §5.1). A pure, DI-free value produced by <see cref="Resolve"/> —
/// constructible and testable with a plain <see cref="IConfiguration"/>, no hosting or DI
/// container required.
/// </summary>
public sealed class DataStorageSettings
{
    public StorageMode Mode { get; set; }

    /// <summary>Set only when <see cref="Mode"/> is <see cref="StorageMode.Local"/>.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Set only when <see cref="Mode"/> is <see cref="StorageMode.Cloud"/>.</summary>
    public string? AccountUrl { get; set; }

    public string BronzeContainer { get; set; } = "bronze";
    public string SilverContainer { get; set; } = "silver";
    public string QuarantineContainer { get; set; } = "quarantine";

    /// <summary>
    /// Resolves data-storage settings from configuration. <c>DATA_STORAGE_CONNECTION</c> wins if
    /// set (even when <c>DATA_STORAGE_ACCOUNT_URL</c> is also present); otherwise
    /// <c>DATA_STORAGE_ACCOUNT_URL</c> is used. A value that is empty or all-whitespace counts as
    /// unset. If neither is set, throws naming both variables.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Neither <c>DATA_STORAGE_CONNECTION</c> nor <c>DATA_STORAGE_ACCOUNT_URL</c> is set.
    /// </exception>
    public static DataStorageSettings Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var bronzeContainer = configuration["BRONZE_CONTAINER"];
        var silverContainer = configuration["SILVER_CONTAINER"];
        var quarantineContainer = configuration["QUARANTINE_CONTAINER"];

        var settings = new DataStorageSettings
        {
            BronzeContainer = string.IsNullOrWhiteSpace(bronzeContainer) ? "bronze" : bronzeContainer,
            SilverContainer = string.IsNullOrWhiteSpace(silverContainer) ? "silver" : silverContainer,
            QuarantineContainer = string.IsNullOrWhiteSpace(quarantineContainer) ? "quarantine" : quarantineContainer,
        };

        var connectionString = configuration["DATA_STORAGE_CONNECTION"];
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            settings.Mode = StorageMode.Local;
            settings.ConnectionString = connectionString;
            return settings;
        }

        var accountUrl = configuration["DATA_STORAGE_ACCOUNT_URL"];
        if (!string.IsNullOrWhiteSpace(accountUrl))
        {
            settings.Mode = StorageMode.Cloud;
            settings.AccountUrl = accountUrl;
            return settings;
        }

        throw new InvalidOperationException(
            "Either DATA_STORAGE_CONNECTION or DATA_STORAGE_ACCOUNT_URL is required.");
    }
}
