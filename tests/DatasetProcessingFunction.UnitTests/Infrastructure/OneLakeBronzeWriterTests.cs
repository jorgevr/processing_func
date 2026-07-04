using DatasetProcessingFunction.Domain.Models;
using DatasetProcessingFunction.Domain.ValueObjects;
using DatasetProcessingFunction.Infrastructure.Storage;
using FluentAssertions;
using Parquet.Data;
using Parquet.Schema;

namespace DatasetProcessingFunction.UnitTests.Infrastructure;

public sealed class OneLakeBronzeWriterTests
{
    private static VendorSchemaMapping BuildMapping(params ColumnMapping[] extra) => new()
    {
        VendorId = "vendor-abc",
        SchemaVersion = "v2",
        Delimiter = ',',
        Encoding = "UTF-8",
        ColumnMappings =
        [
            new ColumnMapping("ts", "timestamp", "datetime", null),
            new ColumnMapping("sid", "site_id", "string", null),
            new ColumnMapping("pwr_kw", "power_w", "decimal", "kw_to_w"),
            ..extra
        ],
        RequiredFields = ["ts", "sid"]
    };

    private static CanonicalRecord MakeRecord(string siteId = "s1", decimal power = 10.5m) =>
        new(
            SiteId: siteId,
            Timestamp: DateTimeOffset.UtcNow,
            IngestionTime: DateTimeOffset.UtcNow,
            SourceDatasetId: "d1",
            SchemaVersion: "v2",
            Fields: new Dictionary<string, object> { ["power_w"] = power });

    [Fact]
    public void BuildSchema_AlwaysContainsFiveEnrichmentColumns()
    {
        var schema = OneLakeBronzeWriter.BuildSchema(BuildMapping());

        schema.DataFields.Should().Contain(f => f.Name == "site_id");
        schema.DataFields.Should().Contain(f => f.Name == "timestamp");
        schema.DataFields.Should().Contain(f => f.Name == "ingestion_time");
        schema.DataFields.Should().Contain(f => f.Name == "source_dataset_id");
        schema.DataFields.Should().Contain(f => f.Name == "schema_version");
    }

    [Fact]
    public void BuildSchema_DatetimeColumns_UseDateTimeDataField()
    {
        var schema = OneLakeBronzeWriter.BuildSchema(BuildMapping());

        schema.Fields.OfType<DateTimeDataField>().Select(f => f.Name)
            .Should().Contain("timestamp")
            .And.Contain("ingestion_time");
    }

    [Fact]
    public void BuildSchema_DecimalColumns_UseDecimalDataField()
    {
        var schema = OneLakeBronzeWriter.BuildSchema(BuildMapping());

        var decField = schema.Fields.OfType<DecimalDataField>()
            .Should().ContainSingle(f => f.Name == "power_w").Subject;
        decField.Precision.Should().Be(18);
        decField.Scale.Should().Be(6);
    }

    [Fact]
    public void BuildSchema_VendorColumnDuplicatingEnrichmentName_IsSkipped()
    {
        // "site_id" is an enrichment column — vendor re-mapping it should NOT produce a duplicate
        var mapping = BuildMapping(new ColumnMapping("raw_sid", "site_id", "string", null));

        var schema = OneLakeBronzeWriter.BuildSchema(mapping);

        schema.DataFields.Count(f => f.Name == "site_id").Should().Be(1);
    }

    [Fact]
    public void BuildSchema_TotalColumnCount_IsEnrichmentPlusNonDuplicateVendorColumns()
    {
        // mapping: ts→timestamp (enrichment dup, skipped), sid→site_id (enrichment dup, skipped), pwr_kw→power_w (kept)
        // enrichment: 5 fixed columns
        // non-duplicate vendor: power_w = 1
        // total = 6
        var schema = OneLakeBronzeWriter.BuildSchema(BuildMapping());

        schema.DataFields.Length.Should().Be(6);
    }

    [Fact]
    public void BuildColumnArray_DecimalType_ReturnsNullableDecimalArray()
    {
        var records = new List<CanonicalRecord>
        {
            new("s1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "d1", "v2",
                new Dictionary<string, object> { ["power_w"] = 10.5m }),
            new("s2", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "d1", "v2",
                new Dictionary<string, object>())
        };

        var result = OneLakeBronzeWriter.BuildColumnArray(records, "power_w", "decimal");

        result.Should().BeOfType<decimal?[]>();
        var arr = (decimal?[])result;
        arr[0].Should().Be(10.5m);
        arr[1].Should().BeNull();
    }

    [Fact]
    public void BuildSchema_PassThroughMode_ExtraColumnsAddedAsStringFields()
    {
        var mapping = BuildMapping();
        mapping.PassThroughUnmapped = true;
        var records = new List<CanonicalRecord>
        {
            new("s1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "d1", "v2",
                new Dictionary<string, object>
                {
                    ["power_w"] = 10.5m,
                    ["dc_current_string_01"] = "3.75"  // pass-through column
                })
        };

        var schema = OneLakeBronzeWriter.BuildSchema(mapping, records);

        schema.DataFields.Should().Contain(f => f.Name == "dc_current_string_01");
        var extraField = schema.DataFields.First(f => f.Name == "dc_current_string_01");
        extraField.ClrType.Should().Be(typeof(string));
    }

    [Fact]
    public void BuildSchema_PassThroughMode_NoExtraColumns_SchemaUnchanged()
    {
        var mapping = BuildMapping();
        mapping.PassThroughUnmapped = true;
        var records = new List<CanonicalRecord>
        {
            MakeRecord()  // Fields only contains power_w (a canonical name)
        };

        var schema = OneLakeBronzeWriter.BuildSchema(mapping, records);

        // power_w is in ColumnMappings → not a pass-through; total = 6 (same as non-pass-through)
        schema.DataFields.Length.Should().Be(6);
    }

    [Fact]
    public void BuildColumnArray_StringType_ReturnsNullableStringArray()
    {
        var records = new List<CanonicalRecord>
        {
            new("site-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "d1", "v2",
                new Dictionary<string, object> { ["label"] = "hello" })
        };

        var result = OneLakeBronzeWriter.BuildColumnArray(records, "label", "string");

        var arr = (string?[])result;
        arr[0].Should().Be("hello");
    }
}
