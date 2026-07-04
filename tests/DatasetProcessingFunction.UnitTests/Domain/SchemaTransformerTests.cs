using DatasetProcessingFunction.Domain.Models;
using DatasetProcessingFunction.Domain.Services;
using DatasetProcessingFunction.Domain.ValueObjects;
using FluentAssertions;

namespace DatasetProcessingFunction.UnitTests.Domain;

public sealed class SchemaTransformerTests
{
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

    private static RawRecord BuildRecord(int rowIndex, Dictionary<string, string> fields) =>
        new(rowIndex, fields);

    [Fact]
    public void Transform_VendorColumnsMapToCanonicalFields()
    {
        var mapping = BuildMapping();
        var records = new List<RawRecord>
        {
            BuildRecord(1, new Dictionary<string, string>
            {
                ["ts"] = "2026-03-15T10:00:00Z",
                ["sid"] = "site-1",
                ["pwr_kw"] = "10.0"
            })
        };

        var result = new SchemaTransformer().Transform(records, mapping);

        result.Should().HaveCount(1);
        result[0].Fields.Should().ContainKey("power_w");
        result[0].Fields.Should().ContainKey("site_id");
    }

    [Fact]
    public void Transform_DatetimeNormalisedToIso8601Utc()
    {
        var mapping = BuildMapping();
        var records = new List<RawRecord>
        {
            BuildRecord(1, new Dictionary<string, string>
            {
                ["ts"] = "2026-03-15T10:00:00",
                ["sid"] = "site-1",
                ["pwr_kw"] = "5.0"
            })
        };

        var result = new SchemaTransformer().Transform(records, mapping);

        result[0].Timestamp.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Transform_KwToWattUnitConversion()
    {
        var mapping = BuildMapping();
        var records = new List<RawRecord>
        {
            BuildRecord(1, new Dictionary<string, string>
            {
                ["ts"] = "2026-03-15T10:00:00Z",
                ["sid"] = "site-1",
                ["pwr_kw"] = "10.5"
            })
        };

        var result = new SchemaTransformer().Transform(records, mapping);

        ((decimal)result[0].Fields["power_w"]).Should().Be(10500m);
    }

    [Fact]
    public void Transform_PassThroughUnmapped_UnmappedColumnsPreservedAsRawStrings()
    {
        var mapping = BuildMapping();
        mapping.PassThroughUnmapped = true;
        var records = new List<RawRecord>
        {
            BuildRecord(1, new Dictionary<string, string>
            {
                ["ts"]  = "2026-03-15T10:00:00Z",
                ["sid"] = "site-1",
                ["pwr_kw"] = "5.0",
                ["dc_current_string_01"] = "3.75"   // not in mapping
            })
        };

        var result = new SchemaTransformer().Transform(records, mapping);

        result[0].Fields.Should().ContainKey("dc_current_string_01");
        result[0].Fields["dc_current_string_01"].Should().Be("3.75");
        // Mapped column still transformed normally
        ((decimal)result[0].Fields["power_w"]).Should().Be(5000m);
    }

    [Fact]
    public void Transform_PassThroughUnmapped_False_UnmappedColumnsDropped()
    {
        var mapping = BuildMapping(); // PassThroughUnmapped = false (default)
        var records = new List<RawRecord>
        {
            BuildRecord(1, new Dictionary<string, string>
            {
                ["ts"]  = "2026-03-15T10:00:00Z",
                ["sid"] = "site-1",
                ["pwr_kw"] = "5.0",
                ["dc_current_string_01"] = "3.75"
            })
        };

        var result = new SchemaTransformer().Transform(records, mapping);

        result[0].Fields.Should().NotContainKey("dc_current_string_01");
    }

    [Fact]
    public void Transform_EnrichmentFieldsSiteIdAndIngestionTimePresent()
    {
        var mapping = BuildMapping();
        var records = new List<RawRecord>
        {
            BuildRecord(1, new Dictionary<string, string>
            {
                ["ts"] = "2026-03-15T10:00:00Z",
                ["sid"] = "site-abc",
                ["pwr_kw"] = "1.0"
            })
        };

        var result = new SchemaTransformer().Transform(records, mapping);

        result[0].SiteId.Should().Be("site-abc");
        result[0].IngestionTime.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }
}
