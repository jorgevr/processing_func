namespace DatasetProcessingFunction.Domain.Models;

public sealed class VendorSchemaMapping
{
    public string VendorId { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = string.Empty;
    public char Delimiter { get; set; } = ',';
    public string Encoding { get; set; } = "UTF-8";
    public List<ColumnMapping> ColumnMappings { get; set; } = new();
    public List<string> RequiredFields { get; set; } = new();
    // When true: column-header check is skipped and any column not in ColumnMappings is
    // preserved as a raw string in Bronze. Allows a single schema to handle datasets
    // from the same vendor that share required fields but have varying measurement columns.
    public bool PassThroughUnmapped { get; set; } = false;
}

public sealed record ColumnMapping(
    string VendorColumn,
    string CanonicalField,
    string DataType,
    string? UnitConversion,
    decimal? MinValue = null,
    decimal? MaxValue = null)
{
    public void EnsureValid()
    {
        if (MinValue.HasValue && MaxValue.HasValue && MinValue.Value > MaxValue.Value)
            throw new ArgumentException(
                $"MinValue ({MinValue}) cannot be greater than MaxValue ({MaxValue}) for column '{VendorColumn}'.");
    }
}
