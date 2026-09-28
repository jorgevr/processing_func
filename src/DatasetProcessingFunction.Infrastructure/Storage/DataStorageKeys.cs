namespace DatasetProcessingFunction.Infrastructure.Storage;

/// <summary>
/// Keyed-DI service keys distinguishing the data-account <c>BlobServiceClient</c> (dataset reads,
/// Bronze writes) from the unkeyed schema-registry <c>BlobServiceClient</c> — two separate storage
/// accounts (docs/contracts.md "Shared configuration").
/// </summary>
public static class DataStorageKeys
{
    public const string DataStorage = "data-storage";
}
