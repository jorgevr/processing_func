using DatasetProcessingFunction.Domain.Models;
using DatasetProcessingFunction.Domain.Services;
using DatasetProcessingFunction.Domain.ValueObjects;
using FluentAssertions;

namespace DatasetProcessingFunction.UnitTests.Domain;

public sealed class DataQualityValidatorTests
{
    private readonly DataQualityValidator _sut = new();

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
    public void Validate_AllFieldsPresent_ReturnsSuccess()
    {
        var records = new List<RawRecord>
        {
            new(1, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ts"] = "2026-03-15T10:00:00Z",
                ["sid"] = "site-1",
                ["pwr_kw"] = "100.5"
            }),
            new(2, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ts"] = "2026-03-15T10:01:00Z",
                ["sid"] = "site-2",
                ["pwr_kw"] = "200.0"
            })
        };

        var result = _sut.Validate(records, BuildMapping());

        result.IsSuccess.Should().BeTrue();
        result.PassCount.Should().Be(2);
        result.FailCount.Should().Be(0);
        result.Failures.Should().BeEmpty();
    }

    [Fact]
    public void Validate_MissingTimestamp_ReturnsFailureWithRequiredFieldRule()
    {
        var records = new List<RawRecord>
        {
            new(1, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ts"] = "",
                ["sid"] = "site-1",
                ["pwr_kw"] = "10.5"
            })
        };

        var result = _sut.Validate(records, BuildMapping());

        result.IsSuccess.Should().BeFalse();
        result.Failures.Should().ContainSingle(f =>
            f.Rule == "RequiredField" && f.FieldName == "ts" && f.RowIndex == 1);
    }

    [Fact]
    public void Validate_InvalidNumericValue_ReturnsFailureWithNumericRangeRule()
    {
        var records = new List<RawRecord>
        {
            new(1, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ts"] = "2026-03-15T10:00:00Z",
                ["sid"] = "site-1",
                ["pwr_kw"] = "not-a-number"
            })
        };

        var result = _sut.Validate(records, BuildMapping());

        result.IsSuccess.Should().BeFalse();
        result.Failures.Should().ContainSingle(f =>
            f.Rule == "NumericRange" && f.FieldName == "pwr_kw" && f.RowIndex == 1);
    }

    [Fact]
    public void Validate_MissingExpectedColumn_ReturnsFailureWithUnknownSchemaRule()
    {
        // Records with headers that don't include a column the mapping expects
        var records = new List<RawRecord>
        {
            new(1, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ts"] = "2026-03-15T10:00:00Z",
                ["sid"] = "site-1"
                // pwr_kw missing entirely
            })
        };

        var result = _sut.Validate(records, BuildMapping());

        result.IsSuccess.Should().BeFalse();
        result.Failures.Should().ContainSingle(f =>
            f.Rule == "UnknownSchema" && f.FieldName == "pwr_kw");
    }

    [Fact]
    public void Validate_MultipleRowsWithMixedErrors_ReportsAllFailures()
    {
        var records = new List<RawRecord>
        {
            new(1, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ts"] = "",           // missing required
                ["sid"] = "site-1",
                ["pwr_kw"] = "100.0"
            }),
            new(2, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ts"] = "2026-03-15T10:01:00Z",
                ["sid"] = "site-2",
                ["pwr_kw"] = "bad-value"   // invalid numeric
            })
        };

        var result = _sut.Validate(records, BuildMapping());

        result.IsSuccess.Should().BeFalse();
        result.Failures.Should().HaveCount(2);
        result.Failures.Should().Contain(f => f.Rule == "RequiredField" && f.RowIndex == 1);
        result.Failures.Should().Contain(f => f.Rule == "NumericRange" && f.RowIndex == 2);
    }
}
