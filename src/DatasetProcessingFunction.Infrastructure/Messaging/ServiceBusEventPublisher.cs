using System.Text.Json;
using Azure.Messaging.ServiceBus;
using DatasetProcessingFunction.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace DatasetProcessingFunction.Infrastructure.Messaging;

public sealed class ServiceBusEventPublisher : IEventPublisher
{
    private readonly ServiceBusSender _sender;
    private readonly ILogger<ServiceBusEventPublisher> _logger;

    public ServiceBusEventPublisher(ServiceBusSender sender, ILogger<ServiceBusEventPublisher> logger)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task PublishAsync(
        string eventType,
        object payload,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Publishing event {EventType} with correlationId {CorrelationId}", eventType, correlationId);

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false
        });

        var message = new ServiceBusMessage(json)
        {
            ContentType = "application/json",
            Subject = eventType,
            CorrelationId = correlationId,
            MessageId = Guid.NewGuid().ToString()
        };

        await _sender.SendMessageAsync(message, cancellationToken);
    }
}
