using System.Diagnostics.Metrics;
using DatasetProcessingFunction.Domain.ValueObjects;
using DatasetProcessingFunction.Infrastructure.Telemetry;
using FluentAssertions;

namespace DatasetProcessingFunction.UnitTests.Infrastructure;

public sealed class ProcessingMetricsEmitterTests : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, long> _counters = new();
    private readonly Dictionary<string, long> _histograms = new();

    public ProcessingMetricsEmitterTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == ProcessingMetricsEmitter.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument is Counter<long>)
                _counters[instrument.Name] = _counters.GetValueOrDefault(instrument.Name) + measurement;
            else if (instrument is Histogram<long>)
                _histograms[instrument.Name] = measurement;
        });

        _listener.Start();
    }

    [Fact]
    public void Emit_ValidMetrics_RecordsAllCounters()
    {
        using var emitter = new ProcessingMetricsEmitter();

        emitter.Emit(new ProcessingMetrics(
            DatasetId: "vendor-abc-20260315-001",
            RecordsProcessed: 10,
            ValidationPassCount: 9,
            ValidationFailCount: 1,
            ProcessingDurationMs: 500,
            BronzePath: new Uri("https://onelake.dfs.fabric.microsoft.com/bronze/data.parquet")));

        _listener.RecordObservableInstruments();

        _counters["records_processed"].Should().Be(10);
        _counters["validation_pass_count"].Should().Be(9);
        _counters["validation_fail_count"].Should().Be(1);
        _histograms["processing_duration_ms"].Should().Be(500);
    }

    [Fact]
    public void Emit_CalledTwice_AccumulatesCounters()
    {
        using var emitter = new ProcessingMetricsEmitter();

        emitter.Emit(new ProcessingMetrics("ds-001", 5, 5, 0, 100, null));
        emitter.Emit(new ProcessingMetrics("ds-002", 3, 2, 1, 200, null));

        _listener.RecordObservableInstruments();

        _counters["records_processed"].Should().Be(8);
        _counters["validation_pass_count"].Should().Be(7);
        _counters["validation_fail_count"].Should().Be(1);
    }

    public void Dispose() => _listener.Dispose();
}
