using System.Text;
using Azure.Messaging.ServiceBus;
using DatasetProcessingFunction.Application.Commands;
using DatasetProcessingFunction.Functions;
using DatasetProcessingFunction.Infrastructure.Contracts;
using FluentAssertions;
using MediatR;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DatasetProcessingFunction.IntegrationTests.Functions;

/// <summary>
/// R3.7 Accept: "a message with type <c>some.other.event.v1</c> dead-letters instead of being
/// processed — today it would be processed." Uses the real <see cref="DatasetAvailableEventValidator"/>
/// (not a mock of the thing being tested) against a real <see cref="ServiceBusReceivedMessage"/> and
/// a real <see cref="ProcessDatasetFunction"/>; only the MediatR/Service-Bus-action seams are mocked.
/// </summary>
public sealed class ProcessDatasetFunctionTests
{
    private const string ValidEnvelope = """
        {
          "specversion": "1.0",
          "type": "solar.pvdaq.dataset.available.v1",
          "source": "/energy-ingestion-boundary/pvdaq",
          "id": "3f2a7c58-9b1e-4d0a-8f6c-2e5b1a7d9c04",
          "time": "2026-09-24T06:12:41.884213+00:00",
          "datacontenttype": "application/json",
          "dataschema": "https://github.com/jorgevr/anomalIA-app/contracts/dataset-available.v1.json",
          "tenant_id": "anomalia-dev",
          "source_vendor": "PVDAQ",
          "schema_version": "v1",
          "mapping_version": "unknown",
          "correlation_id": "b1d4e8f0-6a22-4c17-9e83-5d0c7f31ab99",
          "ingestion_timestamp": "2026-09-24T06:12:41.884213+00:00",
          "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
          "data": {
            "site_id": 9068,
            "category": "ac_power",
            "file_format": "csv",
            "storage_path": "http://127.0.0.1:10000/devstoreaccount1/bronze/source=pvdaq/dataset=9068_ac_power/ingestion_date=2026-09-24/9068_ac_power_v1.csv",
            "version": 1,
            "ingestion_id": "b1d4e8f0-6a22-4c17-9e83-5d0c7f31ab99",
            "source_url": "https://oedi-data-lake.s3.amazonaws.com/pvdaq/2023-solar-data-prize/9068_OEDI/data/9068_ac_power_data.csv",
            "file_size": 1048576,
            "file_hash": "1d4b479980b6a38bd006688e33f804f3f7bcb48ad273a3a4e99e1703e50cbb87"
          }
        }
        """;

    // Same envelope as ValidEnvelope but with the literal type the Accept criterion names.
    private const string WrongTypeEnvelope = """
        {
          "specversion": "1.0",
          "type": "some.other.event.v1",
          "source": "/energy-ingestion-boundary/pvdaq",
          "id": "3f2a7c58-9b1e-4d0a-8f6c-2e5b1a7d9c04",
          "time": "2026-09-24T06:12:41.884213+00:00",
          "datacontenttype": "application/json",
          "dataschema": "https://github.com/jorgevr/anomalIA-app/contracts/dataset-available.v1.json",
          "tenant_id": "anomalia-dev",
          "source_vendor": "PVDAQ",
          "schema_version": "v1",
          "mapping_version": "unknown",
          "correlation_id": "b1d4e8f0-6a22-4c17-9e83-5d0c7f31ab99",
          "ingestion_timestamp": "2026-09-24T06:12:41.884213+00:00",
          "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
          "data": {
            "site_id": 9068,
            "category": "ac_power",
            "file_format": "csv",
            "storage_path": "http://127.0.0.1:10000/devstoreaccount1/bronze/source=pvdaq/dataset=9068_ac_power/ingestion_date=2026-09-24/9068_ac_power_v1.csv",
            "version": 1,
            "ingestion_id": "b1d4e8f0-6a22-4c17-9e83-5d0c7f31ab99",
            "source_url": "https://oedi-data-lake.s3.amazonaws.com/pvdaq/2023-solar-data-prize/9068_OEDI/data/9068_ac_power_data.csv",
            "file_size": 1048576,
            "file_hash": "1d4b479980b6a38bd006688e33f804f3f7bcb48ad273a3a4e99e1703e50cbb87"
          }
        }
        """;

    // Envelope-shape violation (unknown envelope attribute) that is NOT a type problem.
    private const string EnvelopeShapeViolation = """
        {
          "specversion": "1.0",
          "type": "solar.pvdaq.dataset.available.v1",
          "source": "/energy-ingestion-boundary/pvdaq",
          "id": "3f2a7c58-9b1e-4d0a-8f6c-2e5b1a7d9c04",
          "time": "2026-09-24T06:12:41.884213+00:00",
          "datacontenttype": "application/json",
          "dataschema": "https://github.com/jorgevr/anomalIA-app/contracts/dataset-available.v1.json",
          "tenant_id": "anomalia-dev",
          "source_vendor": "PVDAQ",
          "schema_version": "v1",
          "mapping_version": "unknown",
          "correlation_id": "b1d4e8f0-6a22-4c17-9e83-5d0c7f31ab99",
          "ingestion_timestamp": "2026-09-24T06:12:41.884213+00:00",
          "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
          "data": {
            "site_id": 9068,
            "category": "ac_power",
            "file_format": "csv",
            "storage_path": "http://127.0.0.1:10000/devstoreaccount1/bronze/source=pvdaq/dataset=9068_ac_power/ingestion_date=2026-09-24/9068_ac_power_v1.csv",
            "version": 1,
            "ingestion_id": "b1d4e8f0-6a22-4c17-9e83-5d0c7f31ab99",
            "source_url": "https://oedi-data-lake.s3.amazonaws.com/pvdaq/2023-solar-data-prize/9068_OEDI/data/9068_ac_power_data.csv",
            "file_size": 1048576,
            "file_hash": "1d4b479980b6a38bd006688e33f804f3f7bcb48ad273a3a4e99e1703e50cbb87"
          },
          "mapping_hint": "auto"
        }
        """;

    private static ServiceBusReceivedMessage BuildMessage(string json) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData(Encoding.UTF8.GetBytes(json)),
            messageId: Guid.NewGuid().ToString());

    [Fact]
    public async Task RunAsync_UnrecognizedEventType_DeadLettersWithDistinctReason_NeverProcesses()
    {
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var actions = new Mock<ServiceBusMessageActions>();
        var function = new ProcessDatasetFunction(
            mediator.Object, new DatasetAvailableEventValidator(), NullLogger<ProcessDatasetFunction>.Instance);

        await function.RunAsync(BuildMessage(WrongTypeEnvelope), actions.Object, CancellationToken.None);

        // MockBehavior.Strict: any call to Send would itself throw — this is the literal Accept
        // criterion ("today it would be processed"; after the fix it must not be).
        mediator.VerifyNoOtherCalls();

        actions.Verify(a => a.DeadLetterMessageAsync(
            It.IsAny<ServiceBusReceivedMessage>(),
            null,
            "UnrecognizedEventType",
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
        actions.Verify(a => a.CompleteMessageAsync(
            It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_EnvelopeShapeViolation_DeadLettersWithEnvelopeReason_NotTypeReason()
    {
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        var actions = new Mock<ServiceBusMessageActions>();
        var function = new ProcessDatasetFunction(
            mediator.Object, new DatasetAvailableEventValidator(), NullLogger<ProcessDatasetFunction>.Instance);

        await function.RunAsync(BuildMessage(EnvelopeShapeViolation), actions.Object, CancellationToken.None);

        mediator.VerifyNoOtherCalls();

        actions.Verify(a => a.DeadLetterMessageAsync(
            It.IsAny<ServiceBusReceivedMessage>(),
            null,
            "EnvelopeValidationFailed",
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_ValidEnvelope_InvokesMediatorAndCompletesMessage()
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<ProcessDatasetCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProcessDatasetResult.Ok(5, new Uri("http://127.0.0.1:10000/devstoreaccount1/bronze/x/data.parquet")));
        var actions = new Mock<ServiceBusMessageActions>();
        var function = new ProcessDatasetFunction(
            mediator.Object, new DatasetAvailableEventValidator(), NullLogger<ProcessDatasetFunction>.Instance);

        await function.RunAsync(BuildMessage(ValidEnvelope), actions.Object, CancellationToken.None);

        mediator.Verify(m => m.Send(It.IsAny<ProcessDatasetCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        actions.Verify(a => a.CompleteMessageAsync(
            It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        actions.Verify(a => a.DeadLetterMessageAsync(
            It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<Dictionary<string, object>>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
