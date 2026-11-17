using Azure.Messaging.ServiceBus;
using DatasetProcessingFunction.Infrastructure.Messaging;
using FluentAssertions;

namespace DatasetProcessingFunction.UnitTests.Infrastructure.Messaging;

/// <summary>
/// Asserts the classifier against the SDK's real <see cref="ServiceBusFailureReason"/> hierarchy
/// by walking every value the enum currently defines, rather than a hand-maintained list of
/// reasons that would silently stop covering new values the SDK adds later.
/// </summary>
public class ServiceBusExceptionClassifierTests
{
    public static IEnumerable<object[]> AllFailureReasons() =>
        Enum.GetValues<ServiceBusFailureReason>().Select(reason => new object[] { reason });

    [Theory]
    [MemberData(nameof(AllFailureReasons))]
    public void IsTransient_MatchesTheSdksOwnClassification_ForEveryFailureReason(ServiceBusFailureReason reason)
    {
        var exception = new ServiceBusException("simulated failure", reason, "entity");

        ServiceBusExceptionClassifier.IsTransient(exception).Should().Be(exception.IsTransient);
    }

    [Fact]
    public void IsTransient_NonServiceBusException_ReturnsFalse()
    {
        ServiceBusExceptionClassifier.IsTransient(new InvalidOperationException()).Should().BeFalse();
    }
}
