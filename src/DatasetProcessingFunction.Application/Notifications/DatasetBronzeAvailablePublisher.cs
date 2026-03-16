using DatasetProcessingFunction.Application.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DatasetProcessingFunction.Application.Notifications;

public sealed class DatasetBronzeAvailablePublisher : INotificationHandler<DatasetBronzeAvailableNotification>
{
    private readonly IEventPublisher _publisher;
    private readonly ILogger<DatasetBronzeAvailablePublisher> _logger;

    public DatasetBronzeAvailablePublisher(IEventPublisher publisher, ILogger<DatasetBronzeAvailablePublisher> logger)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task Handle(DatasetBronzeAvailableNotification notification, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Publishing dataset.bronze.available for {DatasetId}", notification.DatasetId);

        var payload = new
        {
            dataset_id = notification.DatasetId,
            record_count = notification.RecordCount,
            bronze_path = notification.BronzePath.ToString(),
            schema_version = notification.SchemaVersion,
            correlation_id = notification.CorrelationId,
            published_at = notification.PublishedAt.ToString("O")
        };

        await _publisher.PublishAsync(
            "dataset.bronze.available",
            payload,
            notification.CorrelationId,
            cancellationToken);
    }
}
