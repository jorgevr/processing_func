using System.Diagnostics.Metrics;
using DatasetProcessingFunction.Domain.ValueObjects;

namespace DatasetProcessingFunction.Infrastructure.Telemetry;

/// <summary>
/// Emits processing metrics via System.Diagnostics.Metrics, registered as an OTel instrument
/// in Program.cs. Each counter/histogram is tagged with dataset_id for per-dataset visibility.
/// </summary>
public sealed class ProcessingMetricsEmitter : IDisposable
{
    public static readonly string MeterName = "DatasetProcessingFunction";

    private readonly Meter _meter;
    private readonly Counter<long> _recordsProcessed;
    private readonly Counter<long> _validationPassCount;
    private readonly Counter<long> _validationFailCount;
    private readonly Histogram<long> _processingDurationMs;

    public ProcessingMetricsEmitter()
    {
        _meter = new Meter(MeterName, "1.0.0");
        _recordsProcessed = _meter.CreateCounter<long>("records_processed", "records",
            "Total records successfully processed to Bronze layer.");
        _validationPassCount = _meter.CreateCounter<long>("validation_pass_count", "records",
            "Total records that passed validation.");
        _validationFailCount = _meter.CreateCounter<long>("validation_fail_count", "records",
            "Total records that failed validation.");
        _processingDurationMs = _meter.CreateHistogram<long>("processing_duration_ms", "ms",
            "End-to-end processing duration per dataset.");
    }

    public void Emit(ProcessingMetrics metrics)
    {
        var tag = new KeyValuePair<string, object?>("dataset_id", metrics.DatasetId);
        _recordsProcessed.Add(metrics.RecordsProcessed, tag);
        _validationPassCount.Add(metrics.ValidationPassCount, tag);
        _validationFailCount.Add(metrics.ValidationFailCount, tag);
        _processingDurationMs.Record(metrics.ProcessingDurationMs, tag);
    }

    public void Dispose() => _meter.Dispose();
}
