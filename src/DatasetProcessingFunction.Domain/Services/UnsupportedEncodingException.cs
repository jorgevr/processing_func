namespace DatasetProcessingFunction.Domain.Services;

public sealed class UnsupportedEncodingException : Exception
{
    public string DetectedEncoding { get; }

    public UnsupportedEncodingException(string detectedEncoding)
        : base($"Unsupported encoding detected: {detectedEncoding}. Only UTF-8 is accepted.")
    {
        DetectedEncoding = detectedEncoding;
    }
}
