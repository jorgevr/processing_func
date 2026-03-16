namespace DatasetProcessingFunction.Domain.Models;

public sealed class VendorSchemaMapping
{
    public string VendorId { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = string.Empty;
    public char Delimiter { get; set; } = ',';
    public string Encoding { get; set; } = "UTF-8";
    public List<ColumnMapping> ColumnMappings { get; set; } = new();
    public List<string> RequiredFields { get; set; } = new();
}

public sealed record ColumnMapping(
    string VendorColumn,
    string CanonicalField,
    string DataType,
    string? UnitConversion);
