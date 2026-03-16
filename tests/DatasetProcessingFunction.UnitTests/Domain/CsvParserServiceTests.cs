using System.Text;
using DatasetProcessingFunction.Domain.Services;
using DatasetProcessingFunction.Domain.ValueObjects;
using FluentAssertions;

namespace DatasetProcessingFunction.UnitTests.Domain;

public sealed class CsvParserServiceTests
{
    private readonly CsvParserService _sut = new();

    [Fact]
    public async Task ParseAsync_ValidCsvWithCommaDelimiter_ReturnsAllDataRows()
    {
        var csv = "timestamp,site_id,power_kw\n2026-03-15T10:00:00Z,site-1,100.5\n2026-03-15T10:01:00Z,site-2,200.0";
        using var stream = CreateUtf8Stream(csv);

        var records = await _sut.ParseAsync(stream, ',', CancellationToken.None).ToListAsync();

        records.Should().HaveCount(2);
    }

    [Fact]
    public async Task ParseAsync_ValidCsv_DetectsHeaderRow()
    {
        var csv = "timestamp,site_id,power_kw\n2026-03-15T10:00:00Z,site-1,100.5";
        using var stream = CreateUtf8Stream(csv);

        var records = await _sut.ParseAsync(stream, ',', CancellationToken.None).ToListAsync();

        records[0].Fields.Keys.Should().Contain(new[] { "timestamp", "site_id", "power_kw" });
    }

    [Fact]
    public async Task ParseAsync_ValidCsv_RowIndexStartsAtOne()
    {
        var csv = "col1,col2\nval1,val2\nval3,val4";
        using var stream = CreateUtf8Stream(csv);

        var records = await _sut.ParseAsync(stream, ',', CancellationToken.None).ToListAsync();

        records[0].RowIndex.Should().Be(1);
        records[1].RowIndex.Should().Be(2);
    }

    [Fact]
    public async Task ParseAsync_SemicolonDelimiter_ParsesCorrectly()
    {
        var csv = "timestamp;site_id;power_kw\n2026-03-15T10:00:00Z;site-1;100.5";
        using var stream = CreateUtf8Stream(csv);

        var records = await _sut.ParseAsync(stream, ';', CancellationToken.None).ToListAsync();

        records.Should().HaveCount(1);
        records[0].Fields["power_kw"].Should().Be("100.5");
    }

    [Fact]
    public async Task ParseAsync_NonUtf8Stream_ThrowsUnsupportedEncodingException()
    {
        var latin1Bytes = Encoding.Latin1.GetBytes("timestamp,value\n2026-01-01,caf\xe9");
        using var stream = new MemoryStream(latin1Bytes);

        var act = async () => await _sut.ParseAsync(stream, ',', CancellationToken.None).ToListAsync();

        await act.Should().ThrowAsync<UnsupportedEncodingException>();
    }

    [Fact]
    public async Task ParseAsync_EmptyCsv_ReturnsNoRecords()
    {
        var csv = "timestamp,site_id\n";
        using var stream = CreateUtf8Stream(csv);

        var records = await _sut.ParseAsync(stream, ',', CancellationToken.None).ToListAsync();

        records.Should().BeEmpty();
    }

    private static MemoryStream CreateUtf8Stream(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new MemoryStream(bytes);
    }
}
