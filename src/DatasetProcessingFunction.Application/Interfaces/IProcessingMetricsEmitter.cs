using DatasetProcessingFunction.Domain.ValueObjects;

namespace DatasetProcessingFunction.Application.Interfaces;

/// <summary>Emits per-execution processing metrics as OpenTelemetry instruments (FR-021).</summary>
public interface IProcessingMetricsEmitter
{
    void Emit(ProcessingMetrics metrics);
}
