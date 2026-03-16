using System.Diagnostics;

namespace DatasetProcessingFunction.Domain.Telemetry;

public static class DatasetActivitySource
{
    public static readonly ActivitySource Source = new("DatasetProcessingFunction", "1.0.0");
}
