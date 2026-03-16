using DatasetProcessingFunction.Domain.Aggregates;
using DatasetProcessingFunction.Domain.Enums;
using DatasetProcessingFunction.Domain.ValueObjects;
using FluentAssertions;

namespace DatasetProcessingFunction.UnitTests.Domain;

public sealed class DatasetProcessingJobTests
{
    private static DatasetProcessingJob CreateJob() =>
        new(new DatasetId("dataset-001"), "vendor-abc", "v2",
            new Uri("abfss://raw@store.dfs.core.windows.net/data.csv"));

    [Fact]
    public void Constructor_SetsInitialStatusToReceived()
    {
        var job = CreateJob();
        job.Status.Should().Be(ProcessingStatus.Received);
        job.Id.Value.Should().Be("dataset-001");
        job.VendorId.Should().Be("vendor-abc");
        job.SchemaVersion.Should().Be("v2");
    }

    [Fact]
    public void Constructor_NullId_Throws() =>
        ((Action)(() => new DatasetProcessingJob(null!, "v", "v2",
            new Uri("abfss://raw@s.dfs.core.windows.net/d")))).Should().Throw<ArgumentNullException>();

    [Fact]
    public void Constructor_EmptyVendorId_Throws() =>
        ((Action)(() => new DatasetProcessingJob(new DatasetId("id"), "", "v2",
            new Uri("abfss://raw@s.dfs.core.windows.net/d")))).Should().Throw<ArgumentException>();

    [Fact]
    public void BeginParsing_TransitionsToParsing()
    {
        var job = CreateJob();
        job.BeginParsing();
        job.Status.Should().Be(ProcessingStatus.Parsing);
    }

    [Fact]
    public void RecordParsed_SetsRawRecordsAndTransitionsToValidating()
    {
        var job = CreateJob();
        job.BeginParsing();
        var records = new List<RawRecord>
        {
            new(1, new Dictionary<string, string> { ["ts"] = "2026-03-15T10:00:00Z" })
        };

        job.RecordParsed(records);

        job.Status.Should().Be(ProcessingStatus.Validating);
        job.RawRecords.Should().HaveCount(1);
    }

    [Fact]
    public void RecordValidated_FailResult_TransitionsToFailed()
    {
        var job = CreateJob();
        job.BeginParsing();
        job.RecordParsed([]);
        var failResult = ValidationResult.Failure(
            [new ValidationFailure(1, "ts", "RequiredField", "missing")]);

        job.RecordValidated(failResult);

        job.Status.Should().Be(ProcessingStatus.Failed);
    }

    [Fact]
    public void RecordValidated_SuccessResult_TransitionsToTransforming()
    {
        var job = CreateJob();
        job.BeginParsing();
        job.RecordParsed([]);
        job.RecordValidated(ValidationResult.Success(0));
        job.Status.Should().Be(ProcessingStatus.Transforming);
    }

    [Fact]
    public void RecordTransformed_SetsCanonicalRecordsAndTransitionsToWriting()
    {
        var job = CreateJob();
        job.BeginParsing();
        job.RecordParsed([]);
        job.RecordValidated(ValidationResult.Success(0));
        job.RecordTransformed([]);
        job.Status.Should().Be(ProcessingStatus.Writing);
    }

    [Fact]
    public void RecordWritten_SetsBronzePathAndTransitionsToCompleted()
    {
        var job = CreateJob();
        job.BeginParsing();
        job.RecordParsed([]);
        job.RecordValidated(ValidationResult.Success(0));
        job.RecordTransformed([]);
        var bronzePath = new Uri("https://onelake.dfs.fabric.microsoft.com/bronze/data.parquet");

        job.RecordWritten(bronzePath);

        job.Status.Should().Be(ProcessingStatus.Completed);
        job.BronzePath.Should().Be(bronzePath);
    }

    [Fact]
    public void Fail_TransitionsToFailed()
    {
        var job = CreateJob();
        job.Fail();
        job.Status.Should().Be(ProcessingStatus.Failed);
    }
}
