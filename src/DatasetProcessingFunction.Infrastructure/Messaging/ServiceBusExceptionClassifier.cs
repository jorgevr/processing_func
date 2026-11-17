using Azure.Messaging.ServiceBus;

namespace DatasetProcessingFunction.Infrastructure.Messaging;

/// <summary>
/// Classifies whether a failure from the Service Bus SDK is transient (worth redelivering) by
/// delegating to <see cref="ServiceBusException.IsTransient"/> — the SDK's own classification of
/// its <see cref="ServiceBusFailureReason"/> values — rather than a hand-maintained switch over
/// those reasons, which would silently drift if the SDK added or reclassified one.
/// </summary>
public static class ServiceBusExceptionClassifier
{
    public static bool IsTransient(Exception exception) =>
        exception is ServiceBusException serviceBusException && serviceBusException.IsTransient;
}
