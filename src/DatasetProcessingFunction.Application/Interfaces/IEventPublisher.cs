namespace DatasetProcessingFunction.Application.Interfaces;

/// <summary>Publishes domain events to a message broker (Azure Service Bus).</summary>
public interface IEventPublisher
{
    /// <summary>
    /// Sends a JSON-serialized event message to the configured Service Bus topic.
    /// </summary>
    /// <param name="eventType">Subject/event-type label (e.g. <c>dataset.bronze.available</c>).</param>
    /// <param name="payload">The event payload — will be serialized to JSON with snake_case property names.</param>
    /// <param name="correlationId">W3C trace-context correlation identifier propagated from the inbound trigger.</param>
    /// <param name="cancellationToken">Propagated cancellation token.</param>
    Task PublishAsync(
        string eventType,
        object payload,
        string correlationId,
        CancellationToken cancellationToken = default);
}
