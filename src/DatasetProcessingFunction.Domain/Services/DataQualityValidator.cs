using DatasetProcessingFunction.Domain.Models;
using DatasetProcessingFunction.Domain.ValueObjects;

namespace DatasetProcessingFunction.Domain.Services;

public sealed class DataQualityValidator
{
    public ValidationResult Validate(IReadOnlyList<RawRecord> records, VendorSchemaMapping mapping)
    {
        var failures = new List<ValidationFailure>();
        var headerSet = records.Count > 0 ? records[0].Fields.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>();

        // Schema consistency check — skipped in pass-through mode, where the dataset may have
        // different measurement columns than the mapping (e.g. PVDAQ combiner vs AC power).
        if (!mapping.PassThroughUnmapped)
        {
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
        }

        // Determine the vendor column name mapped to 'timestamp' for per-record validation (FR-009)
        var timestampVendorColumn = mapping.ColumnMappings
            .FirstOrDefault(c => c.CanonicalField.Equals("timestamp", StringComparison.OrdinalIgnoreCase))
            ?.VendorColumn;

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

            // FR-009: per-record timestamp validation — null = RequiredField, non-parseable = NumericParse
            // Note: null/empty is only reported here if the field is NOT already in RequiredFields
            // (to avoid double-reporting when RequiredField check above has already fired).
            if (timestampVendorColumn is not null)
            {
                var tsIsRequired = mapping.RequiredFields.Contains(
                    timestampVendorColumn, StringComparer.OrdinalIgnoreCase);

                if (!record.Fields.TryGetValue(timestampVendorColumn, out var tsRaw)
                    || string.IsNullOrWhiteSpace(tsRaw))
                {
                    // Only report RequiredField here if not already covered by the RequiredFields check
                    if (!tsIsRequired)
                    {
                        failures.Add(new ValidationFailure(
                            record.RowIndex, timestampVendorColumn, "RequiredField",
                            $"Timestamp field '{timestampVendorColumn}' is null or empty at row {record.RowIndex}."));
                    }
                }
                else if (!DateTimeOffset.TryParse(tsRaw,
                             System.Globalization.CultureInfo.InvariantCulture,
                             System.Globalization.DateTimeStyles.AssumeUniversal |
                             System.Globalization.DateTimeStyles.AdjustToUniversal,
                             out _))
                {
                    failures.Add(new ValidationFailure(
                        record.RowIndex, timestampVendorColumn, "NumericParse",
                        $"Timestamp field '{timestampVendorColumn}' value '{tsRaw}' is not a valid datetime at row {record.RowIndex}."));
                }
            }

            // Numeric parse + range check for decimal/double/float fields (FR-010)
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
                        record.RowIndex, colMap.VendorColumn, "NumericParse",
                        $"Field '{colMap.VendorColumn}' value '{rawVal}' is not a valid number at row {record.RowIndex}."));
                    continue;
                }

                // Range boundary check — only when boundaries are defined on the mapping
                if (colMap.MinValue.HasValue && num < colMap.MinValue.Value)
                {
                    failures.Add(new ValidationFailure(
                        record.RowIndex, colMap.VendorColumn, "NumericRange",
                        $"Field '{colMap.VendorColumn}' value {num} is below minimum {colMap.MinValue.Value} at row {record.RowIndex}."));
                }
                else if (colMap.MaxValue.HasValue && num > colMap.MaxValue.Value)
                {
                    failures.Add(new ValidationFailure(
                        record.RowIndex, colMap.VendorColumn, "NumericRange",
                        $"Field '{colMap.VendorColumn}' value {num} exceeds maximum {colMap.MaxValue.Value} at row {record.RowIndex}."));
                }
            }
        }

        return failures.Count > 0
            ? ValidationResult.Failure(failures)
            : ValidationResult.Success(records.Count);
    }
}
