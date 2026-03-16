using DatasetProcessingFunction.Domain.Models;
using DatasetProcessingFunction.Domain.ValueObjects;

namespace DatasetProcessingFunction.Domain.Services;

public sealed class SchemaTransformer
{
    public IReadOnlyList<CanonicalRecord> Transform(
        IReadOnlyList<RawRecord> records,
        VendorSchemaMapping mapping)
    {
        var result = new List<CanonicalRecord>(records.Count);
        var ingestionTime = DateTimeOffset.UtcNow;

        foreach (var record in records)
        {
            var canonicalFields = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            string siteId = string.Empty;
            DateTimeOffset timestamp = DateTimeOffset.UtcNow;

            foreach (var colMap in mapping.ColumnMappings)
            {
                if (!record.Fields.TryGetValue(colMap.VendorColumn, out var rawValue))
                    continue;

                object typedValue = ConvertField(rawValue, colMap.DataType, colMap.UnitConversion);
                canonicalFields[colMap.CanonicalField] = typedValue;

                if (colMap.CanonicalField.Equals("timestamp", StringComparison.OrdinalIgnoreCase)
                    && typedValue is DateTimeOffset dto)
                    timestamp = dto;

                if (colMap.CanonicalField.Equals("site_id", StringComparison.OrdinalIgnoreCase))
                    siteId = rawValue;
            }

            result.Add(new CanonicalRecord(siteId, timestamp, ingestionTime, string.Empty, mapping.SchemaVersion, canonicalFields));
        }

        return result;
    }

    private static object ConvertField(string rawValue, string dataType, string? unitConversion)
    {
        object typed = dataType.ToLowerInvariant() switch
        {
            "datetime" => ParseDateTime(rawValue),
            "decimal" or "double" or "float" => decimal.TryParse(rawValue,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m,
            "int" or "integer" or "long" => long.TryParse(rawValue, out var l) ? l : 0L,
            "bool" or "boolean" => bool.TryParse(rawValue, out var b) && b,
            _ => rawValue
        };

        if (unitConversion is not null && typed is decimal decVal)
            typed = ApplyUnitConversion(decVal, unitConversion);

        return typed;
    }

    private static DateTimeOffset ParseDateTime(string value)
    {
        if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal |
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var dto))
            return dto;
        return DateTimeOffset.UtcNow;
    }

    private static decimal ApplyUnitConversion(decimal value, string conversion) =>
        conversion.ToLowerInvariant() switch
        {
            "kw_to_w" => value * 1000m,
            "mw_to_w" => value * 1_000_000m,
            "kwh_to_wh" => value * 1000m,
            "mwh_to_wh" => value * 1_000_000m,
            _ => value
        };
}
