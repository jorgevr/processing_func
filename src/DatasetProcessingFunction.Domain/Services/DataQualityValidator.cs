using DatasetProcessingFunction.Domain.Models;
using DatasetProcessingFunction.Domain.ValueObjects;

namespace DatasetProcessingFunction.Domain.Services;

public sealed class DataQualityValidator
{
    public ValidationResult Validate(IReadOnlyList<RawRecord> records, VendorSchemaMapping mapping)
    {
        var failures = new List<ValidationFailure>();
        var headerSet = records.Count > 0 ? records[0].Fields.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>();

        // Schema consistency check — all required mapping columns must be present in header
        foreach (var col in mapping.ColumnMappings.Select(c => c.VendorColumn))
        {
            if (!headerSet.Contains(col))
            {
                failures.Add(new ValidationFailure(0, col, "UnknownSchema",
                    $"Expected column '{col}' not found in dataset headers."));
            }
        }

        if (failures.Count > 0)
            return ValidationResult.Failure(failures);

        foreach (var record in records)
        {
            // Required field check
            foreach (var requiredField in mapping.RequiredFields)
            {
                if (!record.Fields.TryGetValue(requiredField, out var value)
                    || string.IsNullOrWhiteSpace(value))
                {
                    failures.Add(new ValidationFailure(
                        record.RowIndex, requiredField, "RequiredField",
                        $"Required field '{requiredField}' is null or empty at row {record.RowIndex}."));
                }
            }

            // Numeric range check for decimal/double fields
            foreach (var colMap in mapping.ColumnMappings.Where(c =>
                c.DataType.Equals("decimal", StringComparison.OrdinalIgnoreCase) ||
                c.DataType.Equals("double", StringComparison.OrdinalIgnoreCase) ||
                c.DataType.Equals("float", StringComparison.OrdinalIgnoreCase)))
            {
                if (!record.Fields.TryGetValue(colMap.VendorColumn, out var rawVal))
                    continue;

                if (!decimal.TryParse(rawVal, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var num))
                {
                    failures.Add(new ValidationFailure(
                        record.RowIndex, colMap.VendorColumn, "NumericRange",
                        $"Field '{colMap.VendorColumn}' value '{rawVal}' is not a valid number at row {record.RowIndex}."));
                }
            }
        }

        return failures.Count > 0
            ? ValidationResult.Failure(failures)
            : ValidationResult.Success(records.Count);
    }
}
