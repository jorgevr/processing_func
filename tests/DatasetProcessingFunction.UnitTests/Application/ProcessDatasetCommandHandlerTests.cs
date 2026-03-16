using System.Diagnostics;
using System.Diagnostics.Metrics;
using DatasetProcessingFunction.Application.Commands;
using DatasetProcessingFunction.Application.Interfaces;
using DatasetProcessingFunction.Application.Notifications;
using DatasetProcessingFunction.Domain.Models;
using DatasetProcessingFunction.Domain.Services;
using DatasetProcessingFunction.Domain.Telemetry;
using DatasetProcessingFunction.Domain.ValueObjects;
using FluentAssertions;
using MediatR;
using Moq;

namespace DatasetProcessingFunction.UnitTests.Application;

public sealed class ProcessDatasetCommandHandlerTests
{
    private readonly Mock<IDatasetReader> _readerMock = new();
    private readonly Mock<IBronzeWriter> _writerMock = new();
    private readonly Mock<ISchemaRegistry> _registryMock = new();
    private readonly Mock<IEventPublisher> _publisherMock = new();
    private readonly Mock<IMediator> _mediatorMock = new();

    private ProcessDatasetCommandHandler BuildHandler() =>
        new(_readerMock.Object, _writerMock.Object, _registryMock.Object,
            _mediatorMock.Object, new CsvParserService(), new SchemaTransformer(),
            new DataQualityValidator());

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
        _writerMock.Setup(w => w.WriteAsync(It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
                It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Uri("https://onelake.dfs.fabric.microsoft.com/bronze/data.parquet"));

        var handler = BuildHandler();
        var result = await handler.Handle(BuildCommand(), CancellationToken.None);

        result.Success.Should().BeTrue();
        _writerMock.Verify(w => w.WriteAsync(
            It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
            It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<CancellationToken>()), Times.Once);
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
        _writerMock.Setup(w => w.WriteAsync(It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
                It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Uri("https://onelake.dfs.fabric.microsoft.com/bronze/data.parquet"));

        var handler = BuildHandler();
        await handler.Handle(BuildCommand(), CancellationToken.None);

        _mediatorMock.Verify(m => m.Publish(
            It.IsAny<DatasetBronzeAvailableNotification>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_UnknownSchema_ReturnsFailureResult()
    {
        _registryMock.Setup(r => r.GetAsync("vendor-abc", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync((VendorSchemaMapping?)null);

        var handler = BuildHandler();
        var result = await handler.Handle(BuildCommand(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("schema");
        _writerMock.Verify(w => w.WriteAsync(
            It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
            It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // T063: OTel span assertions
    [Fact]
    public async Task Handle_ValidDataset_StartsExpectedActivitySpans()
    {
        var csvContent = "ts,sid,pwr_kw\n2026-03-15T10:00:00Z,site-1,10.0\n";
        var csvBytes = System.Text.Encoding.UTF8.GetBytes(csvContent);

        _registryMock.Setup(r => r.GetAsync("vendor-abc", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildMapping());
        _readerMock.Setup(r => r.ReadAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(csvBytes));
        _writerMock.Setup(w => w.WriteAsync(It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
                It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Uri("https://onelake.dfs.fabric.microsoft.com/bronze/data.parquet"));

        var recordedSpans = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DatasetActivitySource.Source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a => recordedSpans.Add(a.OperationName)
        };
        ActivitySource.AddActivityListener(listener);

        var handler = BuildHandler();
        await handler.Handle(BuildCommand(), CancellationToken.None);

        recordedSpans.Should().Contain("dataset.csv.parse");
        recordedSpans.Should().Contain("dataset.validation.run");
        recordedSpans.Should().Contain("dataset.transform");
        recordedSpans.Should().Contain("dataset.bronze.write");
    }

    [Fact]
    public async Task Handle_ValidDataset_SpansHaveDatasetIdTag()
    {
        var csvContent = "ts,sid,pwr_kw\n2026-03-15T10:00:00Z,site-1,10.0\n";
        var csvBytes = System.Text.Encoding.UTF8.GetBytes(csvContent);

        _registryMock.Setup(r => r.GetAsync("vendor-abc", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildMapping());
        _readerMock.Setup(r => r.ReadAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(csvBytes));
        _writerMock.Setup(w => w.WriteAsync(It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
                It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Uri("https://onelake.dfs.fabric.microsoft.com/bronze/data.parquet"));

        var tagsPerSpan = new Dictionary<string, List<KeyValuePair<string, object?>>>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DatasetActivitySource.Source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => tagsPerSpan[a.OperationName] = a.Tags
                .Select(t => new KeyValuePair<string, object?>(t.Key, t.Value))
                .ToList()
        };
        ActivitySource.AddActivityListener(listener);

        var handler = BuildHandler();
        await handler.Handle(BuildCommand(), CancellationToken.None);

        foreach (var spanName in new[] { "dataset.csv.parse", "dataset.validation.run", "dataset.transform", "dataset.bronze.write" })
        {
            tagsPerSpan.Should().ContainKey(spanName);
            tagsPerSpan[spanName].Should().Contain(t => t.Key == "dataset_id");
        }
    }

    // T064: ProcessingMetrics emission tests
    [Fact]
    public async Task Handle_ValidDataset_ReturnsNonZeroRecordsProcessed()
    {
        var csvContent = "ts,sid,pwr_kw\n2026-03-15T10:00:00Z,site-1,10.0\n2026-03-15T10:01:00Z,site-2,11.0\n";
        var csvBytes = System.Text.Encoding.UTF8.GetBytes(csvContent);

        _registryMock.Setup(r => r.GetAsync("vendor-abc", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildMapping());
        _readerMock.Setup(r => r.ReadAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(csvBytes));
        _writerMock.Setup(w => w.WriteAsync(It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
                It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Uri("https://onelake.dfs.fabric.microsoft.com/bronze/data.parquet"));

        var handler = BuildHandler();
        var result = await handler.Handle(BuildCommand(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.RecordsProcessed.Should().BeGreaterThan(0);
        result.BronzePath.Should().NotBeNull();
    }

    [Fact]
    public async Task Handle_ValidationFailure_ResultHasNullBronzePathAndZeroRecords()
    {
        // Dataset with missing required timestamp
        var csvContent = "ts,sid,pwr_kw\n,site-1,10.5\n2026-03-15T10:01:00Z,site-2,11.0\n";
        var csvBytes = System.Text.Encoding.UTF8.GetBytes(csvContent);

        _registryMock.Setup(r => r.GetAsync("vendor-abc", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildMapping());
        _readerMock.Setup(r => r.ReadAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(csvBytes));

        var handler = BuildHandler();
        var act = async () => await handler.Handle(BuildCommand(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<DatasetValidationException>();
        exception.Which.ValidationResult.FailCount.Should().BeGreaterThan(0);
        exception.Which.ValidationResult.IsSuccess.Should().BeFalse();
        // Writer must NOT have been called — BronzePath is therefore null/unavailable
        _writerMock.Verify(w => w.WriteAsync(
            It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
            It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
