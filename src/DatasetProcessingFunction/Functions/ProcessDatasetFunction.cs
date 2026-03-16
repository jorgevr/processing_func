using System.Text.Json;
using Azure.Messaging.ServiceBus;
using DatasetProcessingFunction.Application.Commands;
using MediatR;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DatasetProcessingFunction.Functions;

public sealed class ProcessDatasetFunction
{
    private readonly IMediator _mediator;
    private readonly ILogger<ProcessDatasetFunction> _logger;

    public ProcessDatasetFunction(IMediator mediator, ILogger<ProcessDatasetFunction> logger)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [Function(nameof(ProcessDatasetFunction))]
    public async Task RunAsync(
        [ServiceBusTrigger("dataset-events", "dataset-ingestion",
            Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing message {MessageId} for subject {Subject}",
            message.MessageId, message.Subject);

        DatasetAvailableEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<DatasetAvailableEvent>(
                message.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (evt is null)
                throw new InvalidOperationException("Message body deserialized to null.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize message {MessageId}", message.MessageId);
            await messageActions.DeadLetterMessageAsync(
                message, null, "DeserializationFailed", ex.Message, cancellationToken);
            return;
        }

        var command = new ProcessDatasetCommand(
            evt.DatasetId,
            new Uri(evt.StoragePath),
            evt.VendorId,
            evt.SchemaVersion,
            evt.CorrelationId);

        try
        {
            var result = await _mediator.Send(command, cancellationToken);

            if (result.Success)
            {
                _logger.LogInformation("Dataset {DatasetId} processed. {Count} records → {Path}",
                    evt.DatasetId, result.RecordsProcessed, result.BronzePath);
                await messageActions.CompleteMessageAsync(message, cancellationToken);
            }
            else
            {
                _logger.LogWarning("Dataset {DatasetId} returned failure: {Error}",
                    evt.DatasetId, result.ErrorMessage);
                await messageActions.DeadLetterMessageAsync(
                    message, null, "ProcessingFailed", result.ErrorMessage ?? "Unknown error", cancellationToken);
            }
        }
        catch (DatasetValidationException vex)
        {
            _logger.LogWarning("Dataset {DatasetId} failed validation: {FailCount} failures",
                vex.DatasetId, vex.ValidationResult.FailCount);

            var description = JsonSerializer.Serialize(new
            {
                error_type = "ValidationFailed",
                dataset_id = vex.DatasetId,
                fail_count = vex.ValidationResult.FailCount,
                failures = vex.ValidationResult.Failures.Take(10).Select(f => new
                {
                    row = f.RowIndex,
                    field = f.FieldName,
                    rule = f.Rule,
                    detail = f.Detail
                })
            });

            await messageActions.DeadLetterMessageAsync(
                message, null, "ValidationFailed", description, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing dataset {DatasetId}", evt?.DatasetId);
            throw; // Re-throw to trigger Service Bus retry
        }
    }
}

public sealed record DatasetAvailableEvent(
    string DatasetId,
    string StoragePath,
    string VendorId,
    string SchemaVersion,
    string CorrelationId,
    DateTimeOffset PublishedAt);
