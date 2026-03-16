namespace DatasetProcessingFunction.Domain.ValueObjects;

public sealed record ValidationResult
{
    public bool IsSuccess { get; }
    public int PassCount { get; }
    public int FailCount { get; }
    public IReadOnlyList<ValidationFailure> Failures { get; }

    private ValidationResult(bool isSuccess, int passCount, int failCount, IReadOnlyList<ValidationFailure> failures)
    {
        IsSuccess = isSuccess;
        PassCount = passCount;
        FailCount = failCount;
        Failures = failures;
    }

    public static ValidationResult Success(int passCount) =>
        new(true, passCount, 0, Array.Empty<ValidationFailure>());

    public static ValidationResult Failure(IReadOnlyList<ValidationFailure> failures) =>
        new(false, 0, failures.Count, failures);
}
