using Azure.Messaging.ServiceBus;
using Azure.Storage.Files.DataLake;
using DatasetProcessingFunction.Application.Interfaces;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DatasetProcessingFunction.Functions;

/// <summary>
/// Warmup trigger executed during scale-out to pre-load key singleton DI clients before
/// the first real message is processed (Constitution VIII — no cold-start penalty on first event).
/// </summary>
public sealed class WarmupFunction
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WarmupFunction> _logger;

    public WarmupFunction(IServiceProvider serviceProvider, ILogger<WarmupFunction> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [Function(nameof(WarmupFunction))]
    public Task RunAsync([WarmupTrigger] object warmupContext)
    {
        _logger.LogInformation("Warmup triggered — pre-loading singleton DI clients");

        // Resolve singletons to force initialization (connection pool establishment, credential fetch)
        _ = _serviceProvider.GetRequiredService<DataLakeServiceClient>();
        _ = _serviceProvider.GetRequiredService<ServiceBusClient>();
        _ = _serviceProvider.GetRequiredService<ISchemaRegistry>();

        _logger.LogInformation("Warmup complete");
        return Task.CompletedTask;
    }
}
