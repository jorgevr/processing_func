using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Messaging.ServiceBus;
using DatasetProcessingFunction.Application.Commands;
using DatasetProcessingFunction.Domain.Exceptions;
using DatasetProcessingFunction.Domain.Services;
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
        [ServiceBusTrigger("%SERVICEBUS_QUEUE_NAME%",
            Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing message {MessageId}", message.MessageId);

        DatasetAvailableEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<DatasetAvailableEvent>(
                message.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (evt?.Data is null
                || string.IsNullOrWhiteSpace(evt.Data.StoragePath)
                || string.IsNullOrWhiteSpace(evt.Data.Category)
                || string.IsNullOrWhiteSpace(evt.SourceVendor))
            {
                throw new InvalidOperationException(
                    "Message body is missing required fields: data.storage_path, data.category, or source_vendor.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize message {MessageId}", message.MessageId);
            await messageActions.DeadLetterMessageAsync(
                message, null, "DeserializationFailed", ex.Message, cancellationToken);
            return;
        }

        // FR-003: CorrelationId is optional — generate Guid fallback if absent
        var correlationId = evt.CorrelationId;
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = Guid.NewGuid().ToString();
            _logger.LogWarning(
                "CorrelationId absent from event; generated fallback {CorrelationId}", correlationId);
        }

        var datasetId = $"{evt.Data.SiteId}_{evt.Data.Category}";

        // Ingestion-func sends storage_path with literal spaces in dataset names.
        // Encode spaces before Uri parsing; ParseAdlsUri decodes them back before the DataLake SDK call.
        var encodedStoragePath = evt.Data.StoragePath.Replace(" ", "%20");
        if (!Uri.TryCreate(encodedStoragePath, UriKind.Absolute, out var storagePath))
        {
            _logger.LogError("Invalid storage_path URI in message {MessageId}: {Path}",
                message.MessageId, evt.Data.StoragePath);
            await messageActions.DeadLetterMessageAsync(
                message, null, "InvalidStoragePath",
                $"storage_path is not a valid absolute URI: {evt.Data.StoragePath}",
                cancellationToken);
            return;
        }

        var command = new ProcessDatasetCommand(
            datasetId,
            storagePath,
            evt.SourceVendor,
            evt.SchemaVersion,
            correlationId);

        try
        {
            var result = await _mediator.Send(command, cancellationToken);

            if (result.Success)
            {
                _logger.LogInformation("Dataset {DatasetId} processed. {Count} records → {Path}",
                    datasetId, result.RecordsProcessed, result.BronzePath);
                await messageActions.CompleteMessageAsync(message, cancellationToken);
            }
            else
            {
                _logger.LogWarning("Dataset {DatasetId} returned failure: {Error}",
                    datasetId, result.ErrorMessage);
                await messageActions.DeadLetterMessageAsync(
                    message, null, "ProcessingFailed", result.ErrorMessage ?? "Unknown error", cancellationToken);
            }
        }
        catch (EmptyDatasetException)
        {
            _logger.LogWarning("Dataset {DatasetId} contains no data rows — dead-lettering as EmptyDataset",
                datasetId);
            await messageActions.DeadLetterMessageAsync(
                message, null, "EmptyDataset",
                JsonSerializer.Serialize(new { error_type = "EmptyDataset", dataset_id = datasetId }),
                cancellationToken);
        }
        catch (UnsupportedEncodingException uex)
        {
            _logger.LogWarning("Dataset {DatasetId} has unsupported encoding {Encoding} — dead-lettering",
                datasetId, uex.DetectedEncoding);
            await messageActions.DeadLetterMessageAsync(
                message, null, "UnsupportedEncoding",
                JsonSerializer.Serialize(new
                {
                    error_type = "UnsupportedEncoding",
                    dataset_id = datasetId,
                    detected_encoding = uex.DetectedEncoding
                }),
                cancellationToken);
        }
        catch (DatasetValidationException vex)
        {
            _logger.LogWarning("Dataset {DatasetId} failed validation: {FailCount} failures",
                vex.DatasetId, vex.ValidationResult.FailCount);
            await messageActions.DeadLetterMessageAsync(
                message, null, "ValidationFailed",
                JsonSerializer.Serialize(new
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
                }),
                cancellationToken);
        }
        catch (UnknownSchemaException uex)
        {
            _logger.LogWarning(
                "Unknown schema for vendor {VendorId} version {SchemaVersion} — dead-lettering",
                uex.VendorId, uex.SchemaVersion);
            await messageActions.DeadLetterMessageAsync(
                message, null, "UnknownSchema",
                JsonSerializer.Serialize(new
                {
                    error_type = "UnknownSchema",
                    dataset_id = datasetId,
                    vendor_id = uex.VendorId,
                    schema_version = uex.SchemaVersion
                }),
                cancellationToken);
        }
        catch (Azure.RequestFailedException rfe) when (rfe.Status == 404)
        {
            _logger.LogWarning("Source file not found for dataset {DatasetId}: {StoragePath}",
                datasetId, evt.Data.StoragePath);
            await messageActions.DeadLetterMessageAsync(
                message, null, "SourceFileNotFound",
                JsonSerializer.Serialize(new
                {
                    error_type = "SourceFileNotFound",
                    dataset_id = datasetId,
                    storage_path = evt.Data.StoragePath
                }),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing dataset {DatasetId}", datasetId);
            throw; // Re-throw to trigger Service Bus retry
        }
    }
}

// CloudEvents envelope — top-level fields use snake_case as published by the ingestion boundary
public sealed record DatasetAvailableEvent
{
    [JsonPropertyName("source_vendor")] public string SourceVendor { get; init; } = "";
    [JsonPropertyName("schema_version")] public string SchemaVersion { get; init; } = "";
    [JsonPropertyName("correlation_id")] public string? CorrelationId { get; init; }
    [JsonPropertyName("data")] public DatasetAvailableEventData? Data { get; init; }
}

public sealed record DatasetAvailableEventData
{
    [JsonPropertyName("site_id")] public long SiteId { get; init; }
    [JsonPropertyName("category")] public string Category { get; init; } = "";
    [JsonPropertyName("storage_path")] public string StoragePath { get; init; } = "";
}
