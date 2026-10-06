using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using DatasetProcessingFunction.Application.Interfaces;
using DatasetProcessingFunction.Infrastructure.SchemaRegistry;
using DatasetProcessingFunction.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using Polly.Retry;

namespace DatasetProcessingFunction.Infrastructure.Configuration;

/// <summary>
/// DI registration for the data-storage and schema-registry storage stacks — the only place
/// either is wired up; <c>Program.cs</c> just calls <see cref="AddDataStorage"/> and
/// <see cref="AddSchemaRegistry"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the data-account <see cref="BlobServiceClient"/> (keyed
    /// <see cref="DataStorageKeys.DataStorage"/> — distinct from the schema-registry client; ADR
    /// 0005, docs/contracts.md "Shared configuration" §5.1), its resilience pipeline, and
    /// <see cref="IDatasetReader"/>/<see cref="ISilverWriter"/>.
    ///
    /// <see cref="DataStorageSettings"/> is validated via the options pattern with
    /// <c>ValidateOnStart()</c>: a missing/invalid configuration throws during host start
    /// (<c>IHost.StartAsync</c>), not lazily on the first message or warmup trigger.
    /// </summary>
    public static IServiceCollection AddDataStorage(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DataStorageSettings>()
            .Configure(options =>
            {
                var resolved = DataStorageSettings.Resolve(configuration);
                options.Mode = resolved.Mode;
                options.ConnectionString = resolved.ConnectionString;
                options.AccountUrl = resolved.AccountUrl;
                options.BronzeContainer = resolved.BronzeContainer;
                options.SilverContainer = resolved.SilverContainer;
                options.QuarantineContainer = resolved.QuarantineContainer;
            })
            .ValidateOnStart();

        // Retry disabled (MaxRetries=0) so Polly owns all retry logic (research.md §3).
        services.AddKeyedSingleton(DataStorageKeys.DataStorage, (sp, _) =>
        {
            var settings = sp.GetRequiredService<IOptions<DataStorageSettings>>().Value;
            var options = new BlobClientOptions { Retry = { MaxRetries = 0 } };
            return settings.Mode == StorageMode.Local
                ? new BlobServiceClient(settings.ConnectionString, options)
                : new BlobServiceClient(new Uri(settings.AccountUrl!), new DefaultAzureCredential(), options);
        });

        // Polly v8 resilience pipeline for ADLS transient fault retry (research.md §3).
        // MaxRetryAttempts=2 = 3 total attempts (1 initial + 2 retries); Azure SDK MaxRetries=0
        // avoids multiplicative retries.
        services.AddResiliencePipeline("adls-read", pipeline =>
            pipeline.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxDelay = TimeSpan.FromSeconds(30),
                ShouldHandle = new PredicateBuilder()
                    .Handle<IOException>()
                    .Handle<RequestFailedException>(ex =>
                        ex.Status is 429 or 500 or 503 or 408),
            }));

        services.AddScoped<IDatasetReader>(sp =>
        {
            var blobClient = sp.GetRequiredKeyedService<BlobServiceClient>(DataStorageKeys.DataStorage);
            var pipelineProvider = sp.GetRequiredService<ResiliencePipelineProvider<string>>();
            return new AdlsDatasetReader(blobClient, pipelineProvider,
                sp.GetRequiredService<ILogger<AdlsDatasetReader>>());
        });
        services.AddScoped<ISilverWriter>(sp =>
        {
            var blobClient = sp.GetRequiredKeyedService<BlobServiceClient>(DataStorageKeys.DataStorage);
            var settings = sp.GetRequiredService<IOptions<DataStorageSettings>>().Value;
            return new SilverWriter(blobClient, settings.SilverContainer,
                sp.GetRequiredService<ILogger<SilverWriter>>());
        });

        return services;
    }

    /// <summary>
    /// Registers the schema-registry <see cref="BlobServiceClient"/> (unkeyed — a separate
    /// storage account from the data-storage client above; keying rationale at
    /// <see cref="DataStorageKeys"/>), its resilience pipeline, and <see cref="ISchemaRegistry"/>.
    ///
    /// <see cref="SchemaRegistrySettings"/> is validated via the options pattern with
    /// <c>ValidateOnStart()</c>: a missing/invalid configuration throws during host start, not
    /// lazily on the first message or warmup trigger.
    /// </summary>
    public static IServiceCollection AddSchemaRegistry(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SchemaRegistrySettings>()
            .Configure(options =>
            {
                var resolved = SchemaRegistrySettings.Resolve(configuration);
                options.Mode = resolved.Mode;
                options.ConnectionString = resolved.ConnectionString;
                options.AccountName = resolved.AccountName;
                options.Container = resolved.Container;
            })
            .ValidateOnStart();

        // Unkeyed and registered independently of AddDataStorage's keyed client above — the two
        // storage accounts are never conflated, whichever order these two methods are called in.
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<SchemaRegistrySettings>>().Value;
            var options = new BlobClientOptions { Retry = { MaxRetries = 0 } };
            return settings.Mode == StorageMode.Local
                ? new BlobServiceClient(settings.ConnectionString, options)
                : new BlobServiceClient(
                    new Uri($"https://{settings.AccountName}.blob.core.windows.net"),
                    new DefaultAzureCredential(), options);
        });

        // Polly v8 resilience pipeline for Schema Registry transient fault retry (research.md
        // §4). 404 (blob not found = unknown schema) is NOT in ShouldHandle — propagates
        // immediately as UnknownSchemaException.
        services.AddResiliencePipeline("schema-registry-read", pipeline =>
            pipeline.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxDelay = TimeSpan.FromSeconds(30),
                ShouldHandle = new PredicateBuilder()
                    .Handle<IOException>()
                    .Handle<RequestFailedException>(ex =>
                        ex.Status is 429 or 500 or 503 or 408),
            }));

        services.AddSingleton<ISchemaRegistry>(sp =>
        {
            var blobClient = sp.GetRequiredService<BlobServiceClient>();
            var settings = sp.GetRequiredService<IOptions<SchemaRegistrySettings>>().Value;
            var pipelineProvider = sp.GetRequiredService<ResiliencePipelineProvider<string>>();
            return new BlobSchemaRegistry(blobClient, settings.Container, pipelineProvider,
                sp.GetRequiredService<ILogger<BlobSchemaRegistry>>());
        });

        return services;
    }
}
