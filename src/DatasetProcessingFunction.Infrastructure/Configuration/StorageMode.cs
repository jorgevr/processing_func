namespace DatasetProcessingFunction.Infrastructure.Configuration;

/// <summary>
/// Which credential path a Blob storage client was built with — shared by
/// <see cref="DataStorageSettings"/> and <see cref="SchemaRegistrySettings"/>.
/// </summary>
public enum StorageMode
{
    /// <summary>Connection string (e.g. the local Azurite emulator).</summary>
    Local,

    /// <summary>Account URL/name + <c>DefaultAzureCredential</c> (production).</summary>
    Cloud,
}
