using DatasetProcessingFunction.Domain.Enums;
using DatasetProcessingFunction.Domain.ValueObjects;

namespace DatasetProcessingFunction.Domain.Aggregates;

public sealed class DatasetProcessingJob
{
    public DatasetId Id { get; }
    public string VendorId { get; }
    public string SchemaVersion { get; }
    public Uri StoragePath { get; }
    public ProcessingStatus Status { get; private set; }
    public IReadOnlyList<RawRecord> RawRecords { get; private set; } = Array.Empty<RawRecord>();
    public IReadOnlyList<CanonicalRecord> CanonicalRecords { get; private set; } = Array.Empty<CanonicalRecord>();
    public ValidationResult? ValidationResult { get; private set; }
    public Uri? BronzePath { get; private set; }

    public DatasetProcessingJob(DatasetId id, string vendorId, string schemaVersion, Uri storagePath)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        VendorId = string.IsNullOrWhiteSpace(vendorId) ? throw new ArgumentException("VendorId required.", nameof(vendorId)) : vendorId;
        SchemaVersion = string.IsNullOrWhiteSpace(schemaVersion) ? throw new ArgumentException("SchemaVersion required.", nameof(schemaVersion)) : schemaVersion;
        StoragePath = storagePath ?? throw new ArgumentNullException(nameof(storagePath));
        Status = ProcessingStatus.Received;
    }

    public void BeginParsing() => Transition(ProcessingStatus.Parsing);

    public void RecordParsed(IReadOnlyList<RawRecord> records)
    {
        RawRecords = records ?? throw new ArgumentNullException(nameof(records));
        Transition(ProcessingStatus.Validating);
    }

    public void RecordValidated(ValidationResult result)
    {
        ValidationResult = result ?? throw new ArgumentNullException(nameof(result));
        if (!result.IsSuccess)
        {
            Status = ProcessingStatus.Failed;
            return;
        }
        Transition(ProcessingStatus.Transforming);
    }

    public void RecordTransformed(IReadOnlyList<CanonicalRecord> records)
    {
        CanonicalRecords = records ?? throw new ArgumentNullException(nameof(records));
        Transition(ProcessingStatus.Writing);
    }

    public void RecordWritten(Uri bronzePath)
    {
        BronzePath = bronzePath ?? throw new ArgumentNullException(nameof(bronzePath));
        Transition(ProcessingStatus.Completed);
    }

    public void Fail() => Status = ProcessingStatus.Failed;

    private void Transition(ProcessingStatus next) => Status = next;
}
