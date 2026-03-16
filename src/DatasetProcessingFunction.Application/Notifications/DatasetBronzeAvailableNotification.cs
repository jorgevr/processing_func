using MediatR;

namespace DatasetProcessingFunction.Application.Notifications;

public sealed record DatasetBronzeAvailableNotification(
    string DatasetId,
    int RecordCount,
    Uri BronzePath,
    string SchemaVersion,
    string CorrelationId,
    DateTimeOffset PublishedAt) : INotification;
