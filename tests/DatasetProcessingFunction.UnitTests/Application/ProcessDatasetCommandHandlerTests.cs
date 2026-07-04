using System.Diagnostics;
using DatasetProcessingFunction.Application.Commands;
using DatasetProcessingFunction.Application.Interfaces;
using DatasetProcessingFunction.Application.Notifications;
using DatasetProcessingFunction.Domain.Models;
using DatasetProcessingFunction.Domain.Services;
using DatasetProcessingFunction.Domain.Telemetry;
using DatasetProcessingFunction.Domain.ValueObjects;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DatasetProcessingFunction.UnitTests.Application;

public sealed class ProcessDatasetCommandHandlerTests
{
    private readonly Mock<IDatasetReader> _readerMock = new();
    private readonly Mock<IBronzeWriter> _writerMock = new();
    private readonly Mock<ISchemaRegistry> _registryMock = new();
    private readonly Mock<IMediator> _mediatorMock = new();
    private readonly Mock<IProcessingMetricsEmitter> _metricsEmitterMock = new();

    private ProcessDatasetCommandHandler BuildHandler() =>
        new(_readerMock.Object, _writerMock.Object, _registryMock.Object,
            _mediatorMock.Object, new CsvParserService(), new SchemaTransformer(),
            new DataQualityValidator(), new RecordEnricher(), _metricsEmitterMock.Object,
            NullLogger<ProcessDatasetCommandHandler>.Instance);

    private static ProcessDatasetCommand BuildCommand() => new(
        "vendor-abc-20260315-001",
        new Uri("abfss://raw@store.dfs.core.windows.net/datasets/vendor-abc/20260315/data.csv"),
        "vendor-abc",
        "v2",
        "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");

    private static VendorSchemaMapping BuildMapping() => new()
    {
        VendorId = "vendor-abc",
        SchemaVersion = "v2",
        Delimiter = ',',
        Encoding = "UTF-8",
        ColumnMappings =
        [
            new ColumnMapping("ts", "timestamp", "datetime", null),
            new ColumnMapping("sid", "site_id", "string", null),
            new ColumnMapping("pwr_kw", "power_w", "decimal", "kw_to_w")
        ],
        RequiredFields = ["ts", "sid"]
    };

    [Fact]
    public async Task Handle_ValidDataset_CallsBronzeWriterOnce()
    {
        var csvContent = "ts,sid,pwr_kw\n2026-03-15T10:00:00Z,site-1,10.0\n";
        var csvBytes = System.Text.Encoding.UTF8.GetBytes(csvContent);

        _registryMock.Setup(r => r.GetAsync("vendor-abc", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildMapping());
        _readerMock.Setup(r => r.ReadAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(csvBytes));
        _writerMock.Setup(w => w.WriteAsync(
                It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
                It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<VendorSchemaMapping>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Uri("https://onelake.dfs.fabric.microsoft.com/bronze/data.parquet"));

        var result = await BuildHandler().Handle(BuildCommand(), CancellationToken.None);

        result.Success.Should().BeTrue();
        _writerMock.Verify(w => w.WriteAsync(
            It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
            It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<VendorSchemaMapping>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ValidDataset_PublishesBronzeAvailableNotification()
    {
        var csvContent = "ts,sid,pwr_kw\n2026-03-15T10:00:00Z,site-1,10.0\n";
        var csvBytes = System.Text.Encoding.UTF8.GetBytes(csvContent);

        _registryMock.Setup(r => r.GetAsync("vendor-abc", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildMapping());
        _readerMock.Setup(r => r.ReadAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(csvBytes));
        _writerMock.Setup(w => w.WriteAsync(
                It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
                It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<VendorSchemaMapping>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Uri("https://onelake.dfs.fabric.microsoft.com/bronze/data.parquet"));

        await BuildHandler().Handle(BuildCommand(), CancellationToken.None);

        _mediatorMock.Verify(m => m.Publish(
            It.IsAny<DatasetBronzeAvailableNotification>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ValidDataset_EmitsProcessingMetrics()
    {
        var csvContent = "ts,sid,pwr_kw\n2026-03-15T10:00:00Z,site-1,10.0\n";
        var csvBytes = System.Text.Encoding.UTF8.GetBytes(csvContent);

        _registryMock.Setup(r => r.GetAsync("vendor-abc", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildMapping());
        _readerMock.Setup(r => r.ReadAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(csvBytes));
        _writerMock.Setup(w => w.WriteAsync(
                It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
                It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<VendorSchemaMapping>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Uri("https://onelake.dfs.fabric.microsoft.com/bronze/data.parquet"));

        await BuildHandler().Handle(BuildCommand(), CancellationToken.None);

        _metricsEmitterMock.Verify(m => m.Emit(It.Is<ProcessingMetrics>(pm =>
            pm.DatasetId == "vendor-abc-20260315-001" &&
            pm.RecordsProcessed == 1 &&
            pm.ValidationPassCount == 1 &&
            pm.ValidationFailCount == 0)), Times.Once);
    }

    [Fact]
    public async Task Handle_UnknownSchema_ThrowsUnknownSchemaException()
    {
        // Registry returns null → handler throws UnknownSchemaException (FR-016)
        _registryMock.Setup(r => r.GetAsync("vendor-abc", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync((VendorSchemaMapping?)null);

        var act = async () => await BuildHandler().Handle(BuildCommand(), CancellationToken.None);

        await act.Should().ThrowAsync<DatasetProcessingFunction.Domain.Exceptions.UnknownSchemaException>();
        _writerMock.Verify(w => w.WriteAsync(
            It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
            It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<VendorSchemaMapping>(), It.IsAny<CancellationToken>()), Times.Never);
        _metricsEmitterMock.Verify(m => m.Emit(It.IsAny<ProcessingMetrics>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ValidDataset_StartsExpectedActivitySpans()
    {
        var csvContent = "ts,sid,pwr_kw\n2026-03-15T10:00:00Z,site-1,10.0\n";
        var csvBytes = System.Text.Encoding.UTF8.GetBytes(csvContent);

        _registryMock.Setup(r => r.GetAsync("vendor-abc", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildMapping());
        _readerMock.Setup(r => r.ReadAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(csvBytes));
        _writerMock.Setup(w => w.WriteAsync(
                It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
                It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<VendorSchemaMapping>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Uri("https://onelake.dfs.fabric.microsoft.com/bronze/data.parquet"));

        var recordedSpans = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DatasetActivitySource.Source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a => recordedSpans.Add(a.OperationName)
        };
        ActivitySource.AddActivityListener(listener);

        await BuildHandler().Handle(BuildCommand(), CancellationToken.None);

        recordedSpans.Should().Contain("dataset.csv.parse");
        recordedSpans.Should().Contain("dataset.validation.run");
        recordedSpans.Should().Contain("dataset.transform");
        recordedSpans.Should().Contain("dataset.bronze.write");
    }

    [Fact]
    public async Task Handle_ValidDataset_RootSpanHasCorrelationIdTag()
    {
        var csvContent = "ts,sid,pwr_kw\n2026-03-15T10:00:00Z,site-1,10.0\n";
        var csvBytes = System.Text.Encoding.UTF8.GetBytes(csvContent);
        const string correlationId = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

        _registryMock.Setup(r => r.GetAsync("vendor-abc", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildMapping());
        _readerMock.Setup(r => r.ReadAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(csvBytes));
        _writerMock.Setup(w => w.WriteAsync(
                It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
                It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<VendorSchemaMapping>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Uri("https://onelake.dfs.fabric.microsoft.com/bronze/data.parquet"));

        Activity? rootSpan = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DatasetActivitySource.Source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => { if (a.OperationName == "dataset.process") rootSpan = a; }
        };
        ActivitySource.AddActivityListener(listener);

        await BuildHandler().Handle(BuildCommand(), CancellationToken.None);

        rootSpan.Should().NotBeNull();
        rootSpan!.Tags.Should().Contain(t => t.Key == "correlation_id" && t.Value == correlationId);
    }

    [Fact]
    public async Task Handle_ValidDataset_WriteAsyncReceivesVendorSchemaMapping()
    {
        var csvContent = "ts,sid,pwr_kw\n2026-03-15T10:00:00Z,site-1,10.0\n";
        var csvBytes = System.Text.Encoding.UTF8.GetBytes(csvContent);
        var mapping = BuildMapping();

        _registryMock.Setup(r => r.GetAsync("vendor-abc", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(mapping);
        _readerMock.Setup(r => r.ReadAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(csvBytes));
        _writerMock.Setup(w => w.WriteAsync(
                It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
                It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<VendorSchemaMapping>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Uri("https://onelake.dfs.fabric.microsoft.com/bronze/data.parquet"));

        await BuildHandler().Handle(BuildCommand(), CancellationToken.None);

        // Verify WriteAsync was called with the same mapping object returned from the registry (FR-017b)
        _writerMock.Verify(w => w.WriteAsync(
            It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
            It.IsAny<IReadOnlyList<CanonicalRecord>>(),
            mapping,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_GuidFallbackCorrelationId_ProcessesNormally_SpanTagPresent()
    {
        // FR-003: when CorrelationId was absent on the event, ProcessDatasetFunction generates a Guid
        // and passes it to the command. The handler must process normally and tag the span.
        var guidCorrelationId = Guid.NewGuid().ToString();
        var csvContent = "ts,sid,pwr_kw\n2026-03-15T10:00:00Z,site-1,10.0\n";
        var csvBytes = System.Text.Encoding.UTF8.GetBytes(csvContent);

        _registryMock.Setup(r => r.GetAsync("vendor-abc", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildMapping());
        _readerMock.Setup(r => r.ReadAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(csvBytes));
        _writerMock.Setup(w => w.WriteAsync(
                It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
                It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<VendorSchemaMapping>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Uri("https://onelake.dfs.fabric.microsoft.com/bronze/data.parquet"));

        var command = new ProcessDatasetCommand(
            "vendor-abc-20260315-001",
            new Uri("abfss://raw@store.dfs.core.windows.net/datasets/vendor-abc/20260315/data.csv"),
            "vendor-abc", "v2", guidCorrelationId);

        Activity? rootSpan = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DatasetActivitySource.Source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => { if (a.OperationName == "dataset.process") rootSpan = a; }
        };
        ActivitySource.AddActivityListener(listener);

        var result = await BuildHandler().Handle(command, CancellationToken.None);

        result.Success.Should().BeTrue("processing should succeed regardless of CorrelationId format");
        rootSpan.Should().NotBeNull();
        rootSpan!.Tags.Should().Contain(t => t.Key == "correlation_id" && t.Value == guidCorrelationId);
    }

    [Fact]
    public async Task Handle_ValidationFailure_ThrowsDatasetValidationException_NoBronzeWrite()
    {
        var csvContent = "ts,sid,pwr_kw\n,site-1,10.5\n2026-03-15T10:01:00Z,site-2,11.0\n";
        var csvBytes = System.Text.Encoding.UTF8.GetBytes(csvContent);

        _registryMock.Setup(r => r.GetAsync("vendor-abc", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildMapping());
        _readerMock.Setup(r => r.ReadAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(csvBytes));

        var act = async () => await BuildHandler().Handle(BuildCommand(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<DatasetValidationException>();
        exception.Which.ValidationResult.FailCount.Should().BeGreaterThan(0);
        exception.Which.ValidationResult.IsSuccess.Should().BeFalse();
        _writerMock.Verify(w => w.WriteAsync(
            It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
            It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<VendorSchemaMapping>(), It.IsAny<CancellationToken>()), Times.Never);
        _metricsEmitterMock.Verify(m => m.Emit(It.IsAny<ProcessingMetrics>()), Times.Never);
    }
}
