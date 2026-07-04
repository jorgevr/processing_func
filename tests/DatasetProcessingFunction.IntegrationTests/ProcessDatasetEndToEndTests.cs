using System.Text;
using DatasetProcessingFunction.Application.Commands;
using DatasetProcessingFunction.Application.Interfaces;
using DatasetProcessingFunction.Domain.Exceptions;
using DatasetProcessingFunction.Domain.Models;
using DatasetProcessingFunction.Domain.Services;
using DatasetProcessingFunction.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DatasetProcessingFunction.IntegrationTests;

/// <summary>
/// End-to-end integration tests. These require Docker (Azurite + Service Bus emulator) running.
/// Run with: dotnet test tests/DatasetProcessingFunction.IntegrationTests/
/// </summary>
public sealed class ProcessDatasetEndToEndTests
{
    private static readonly string FixturesDir =
        Path.Combine(AppContext.BaseDirectory, "fixtures");

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

    private static ProcessDatasetCommandHandler BuildHandler(
        Mock<IDatasetReader> readerMock,
        Mock<IBronzeWriter> writerMock,
        Mock<ISchemaRegistry> registryMock,
        Mock<MediatR.IMediator> mediatorMock)
    {
        var metricsEmitterMock = new Mock<IProcessingMetricsEmitter>();
        return new ProcessDatasetCommandHandler(
            readerMock.Object, writerMock.Object, registryMock.Object,
            mediatorMock.Object, new CsvParserService(), new SchemaTransformer(),
            new DataQualityValidator(), new RecordEnricher(), metricsEmitterMock.Object,
            NullLogger<ProcessDatasetCommandHandler>.Instance);
    }

    [Fact]
    public async Task ValidDataset_ProcessesSuccessfully_WritesToBronze()
    {
        var csvBytes = await File.ReadAllBytesAsync(Path.Combine(FixturesDir, "vendor-abc-v2-valid.csv"));

        var readerMock = new Mock<IDatasetReader>();
        readerMock.Setup(r => r.ReadAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(csvBytes));

        var writerMock = new Mock<IBronzeWriter>();
        writerMock.Setup(w => w.WriteAsync(
                It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
                It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<VendorSchemaMapping>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Uri("https://onelake.dfs.fabric.microsoft.com/bronze/test.parquet"));

        var registryMock = new Mock<ISchemaRegistry>();
        registryMock.Setup(r => r.GetAsync("vendor-abc", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildMapping());

        var mediatorMock = new Mock<MediatR.IMediator>();
        mediatorMock.Setup(m => m.Publish(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = BuildHandler(readerMock, writerMock, registryMock, mediatorMock);

        var result = await handler.Handle(
            new ProcessDatasetCommand(
                "vendor-abc-20260315-001",
                new Uri("abfss://raw@store.dfs.core.windows.net/datasets/vendor-abc/20260315/data.csv"),
                "vendor-abc", "v2",
                "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.RecordsProcessed.Should().Be(5);
        writerMock.Verify(w => w.WriteAsync(
            It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
            It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<VendorSchemaMapping>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MissingTimestampDataset_ThrowsValidationException_NoBronzeWrite()
    {
        var csvBytes = await File.ReadAllBytesAsync(
            Path.Combine(FixturesDir, "vendor-abc-v2-missing-timestamp.csv"));

        var readerMock = new Mock<IDatasetReader>();
        readerMock.Setup(r => r.ReadAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(csvBytes));

        var writerMock = new Mock<IBronzeWriter>();
        var registryMock = new Mock<ISchemaRegistry>();
        registryMock.Setup(r => r.GetAsync("vendor-abc", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildMapping());

        var mediatorMock = new Mock<MediatR.IMediator>();
        var handler = BuildHandler(readerMock, writerMock, registryMock, mediatorMock);

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
            It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<VendorSchemaMapping>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InvalidNumericDataset_ThrowsValidationException_NoBronzeWrite()
    {
        var csvBytes = await File.ReadAllBytesAsync(
            Path.Combine(FixturesDir, "vendor-abc-v2-invalid-range.csv"));

        var readerMock = new Mock<IDatasetReader>();
        readerMock.Setup(r => r.ReadAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(csvBytes));

        var writerMock = new Mock<IBronzeWriter>();
        var registryMock = new Mock<ISchemaRegistry>();
        registryMock.Setup(r => r.GetAsync("vendor-abc", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildMapping());

        var mediatorMock = new Mock<MediatR.IMediator>();
        var handler = BuildHandler(readerMock, writerMock, registryMock, mediatorMock);

        var act = async () => await handler.Handle(
            new ProcessDatasetCommand(
                "vendor-abc-20260315-003",
                new Uri("abfss://raw@store.dfs.core.windows.net/datasets/vendor-abc/20260315/bad-range.csv"),
                "vendor-abc", "v2",
                "00-abc123-xyz789-01"),
            CancellationToken.None);

        // vendor-abc-v2-invalid-range.csv contains "not-a-number" → parse failure (NumericParse)
        await act.Should().ThrowAsync<DatasetValidationException>()
            .Where(e => e.ValidationResult.Failures.Any(f => f.Rule == "NumericParse" && f.FieldName == "pwr_kw"));

        writerMock.Verify(w => w.WriteAsync(
            It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
            It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<VendorSchemaMapping>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmptyDataset_ThrowsEmptyDatasetException_NoBronzeWrite()
    {
        var csvBytes = await File.ReadAllBytesAsync(
            Path.Combine(FixturesDir, "vendor-abc-v2-empty.csv"));

        var readerMock = new Mock<IDatasetReader>();
        readerMock.Setup(r => r.ReadAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(csvBytes));

        var writerMock = new Mock<IBronzeWriter>();
        var registryMock = new Mock<ISchemaRegistry>();
        registryMock.Setup(r => r.GetAsync("vendor-abc", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildMapping());

        var mediatorMock = new Mock<MediatR.IMediator>();
        var handler = BuildHandler(readerMock, writerMock, registryMock, mediatorMock);

        var act = async () => await handler.Handle(
            new ProcessDatasetCommand(
                "vendor-abc-20260315-004",
                new Uri("abfss://raw@store.dfs.core.windows.net/datasets/vendor-abc/20260315/empty.csv"),
                "vendor-abc", "v2",
                "00-abc123-empty-01"),
            CancellationToken.None);

        await act.Should().ThrowAsync<EmptyDatasetException>();

        writerMock.Verify(w => w.WriteAsync(
            It.IsAny<DatasetId>(), It.IsAny<DateOnly>(),
            It.IsAny<IReadOnlyList<CanonicalRecord>>(), It.IsAny<VendorSchemaMapping>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
