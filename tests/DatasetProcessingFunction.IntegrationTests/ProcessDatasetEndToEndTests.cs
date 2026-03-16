using System.Text;
using DatasetProcessingFunction.Application.Commands;
using DatasetProcessingFunction.Application.Interfaces;
using DatasetProcessingFunction.Domain.Models;
using DatasetProcessingFunction.Domain.Services;
using DatasetProcessingFunction.Domain.ValueObjects;
using FluentAssertions;
using Moq;

namespace DatasetProcessingFunction.IntegrationTests;

/// <summary>
/// End-to-end integration tests. These require Docker (Azurite + Service Bus emulator) running.
/// Run with: dotnet test tests/DatasetProcessingFunction.IntegrationTests/
/// </summary>
public sealed class ProcessDatasetEndToEndTests
{
    private static readonly string ValidCsvPath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "vendor-abc-v2-valid.csv");

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
    public async Task ValidDataset_ProcessesSuccessfully_WritesToBronze()
    {
        // Arrange
        var csvBytes = await File.ReadAllBytesAsync(ValidCsvPath);

        var readerMock = new Mock<IDatasetReader>();
        readerMock.Setup(r => r.ReadAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(csvBytes));

        Uri? writtenPath = null;
        var writerMock = new Mock<IBronzeWriter>();
        writerMock.Setup(w => w.WriteAsync(
                It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
                It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<CancellationToken>()))
            .Callback<DatasetId, DateOnly, IReadOnlyList<CanonicalRecord>, CancellationToken>(
                (_, _, _, _) => writtenPath = new Uri("https://onelake.dfs.fabric.microsoft.com/bronze/test.parquet"))
            .ReturnsAsync(() => writtenPath!);

        var registryMock = new Mock<ISchemaRegistry>();
        registryMock.Setup(r => r.GetAsync("vendor-abc", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildMapping());

        var publishedEvents = new List<string>();
        var publisherMock = new Mock<IEventPublisher>();
        publisherMock.Setup(p => p.PublishAsync(
                It.IsAny<string>(), It.IsAny<object>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, object, string, CancellationToken>((eventType, _, _, _) => publishedEvents.Add(eventType))
            .Returns(Task.CompletedTask);

        // Act
        var mediatorMock = new Moq.Mock<MediatR.IMediator>();
        mediatorMock.Setup(m => m.Publish(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = new ProcessDatasetCommandHandler(
            readerMock.Object, writerMock.Object, registryMock.Object,
            mediatorMock.Object, new CsvParserService(), new SchemaTransformer(),
            new DataQualityValidator());

        var result = await handler.Handle(
            new ProcessDatasetCommand(
                "vendor-abc-20260315-001",
                new Uri("abfss://raw@store.dfs.core.windows.net/datasets/vendor-abc/20260315/data.csv"),
                "vendor-abc", "v2",
                "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"),
            CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.RecordsProcessed.Should().Be(5);
        writerMock.Verify(w => w.WriteAsync(
            It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
            It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MissingTimestampDataset_ThrowsValidationException_NoBronzeWrite()
    {
        var csvBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "vendor-abc-v2-missing-timestamp.csv"));

        var readerMock = new Mock<IDatasetReader>();
        readerMock.Setup(r => r.ReadAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(csvBytes));

        var writerMock = new Mock<IBronzeWriter>();
        var registryMock = new Mock<ISchemaRegistry>();
        registryMock.Setup(r => r.GetAsync("vendor-abc", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildMapping());

        var mediatorMock = new Moq.Mock<MediatR.IMediator>();
        var handler = new ProcessDatasetCommandHandler(
            readerMock.Object, writerMock.Object, registryMock.Object,
            mediatorMock.Object, new CsvParserService(), new SchemaTransformer(),
            new DataQualityValidator());

        var act = async () => await handler.Handle(
            new ProcessDatasetCommand(
                "vendor-abc-20260315-002",
                new Uri("abfss://raw@store.dfs.core.windows.net/datasets/vendor-abc/20260315/bad.csv"),
                "vendor-abc", "v2",
                "00-abc123-xyz456-01"),
            CancellationToken.None);

        await act.Should().ThrowAsync<DatasetValidationException>()
            .Where(e => e.ValidationResult.FailCount > 0 &&
                        e.ValidationResult.Failures.Any(f => f.Rule == "RequiredField" && f.FieldName == "ts"));

        writerMock.Verify(w => w.WriteAsync(
            It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
            It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InvalidNumericDataset_ThrowsValidationException_NoBronzeWrite()
    {
        var csvBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "vendor-abc-v2-invalid-range.csv"));

        var readerMock = new Mock<IDatasetReader>();
        readerMock.Setup(r => r.ReadAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(csvBytes));

        var writerMock = new Mock<IBronzeWriter>();
        var registryMock = new Mock<ISchemaRegistry>();
        registryMock.Setup(r => r.GetAsync("vendor-abc", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildMapping());

        var mediatorMock = new Moq.Mock<MediatR.IMediator>();
        var handler = new ProcessDatasetCommandHandler(
            readerMock.Object, writerMock.Object, registryMock.Object,
            mediatorMock.Object, new CsvParserService(), new SchemaTransformer(),
            new DataQualityValidator());

        var act = async () => await handler.Handle(
            new ProcessDatasetCommand(
                "vendor-abc-20260315-003",
                new Uri("abfss://raw@store.dfs.core.windows.net/datasets/vendor-abc/20260315/bad-range.csv"),
                "vendor-abc", "v2",
                "00-abc123-xyz789-01"),
            CancellationToken.None);

        await act.Should().ThrowAsync<DatasetValidationException>()
            .Where(e => e.ValidationResult.Failures.Any(f => f.Rule == "NumericRange" && f.FieldName == "pwr_kw"));

        writerMock.Verify(w => w.WriteAsync(
            It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
            It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
