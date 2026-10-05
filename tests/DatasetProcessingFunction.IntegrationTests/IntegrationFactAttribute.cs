namespace DatasetProcessingFunction.IntegrationTests;

/// <summary>
/// A <see cref="FactAttribute"/> that requires a live Azurite (or other real Blob endpoint) to be
/// reachable. Skips — with an explicit, named reason — rather than running and silently passing
/// when <see cref="AzuriteConnectionStringVariable"/> is not set; it must never look like the test
/// actually verified anything when it did not run.
/// </summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    /// <summary>
    /// The environment variable this attribute checks — the same one Program.cs's
    /// <c>AddDataStorage</c> reads for the local/emulator path (docs/contracts.md "Shared
    /// configuration" §5.1), so running against the docker-compose stack needs no second setting:
    /// e.g. <c>DATA_STORAGE_CONNECTION=UseDevelopmentStorage=true</c> with Azurite's blob port
    /// (10000) reachable from the host running the tests.
    /// </summary>
    public const string AzuriteConnectionStringVariable = "DATA_STORAGE_CONNECTION";

    public IntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AzuriteConnectionStringVariable)))
        {
            Skip =
                $"Set {AzuriteConnectionStringVariable} to a live Azurite (or other Blob) connection " +
                "string to run this test — e.g. 'UseDevelopmentStorage=true' with the docker-compose " +
                "stack's Azurite container up (azurite's blob port 10000 published to the host).";
        }
    }
}
