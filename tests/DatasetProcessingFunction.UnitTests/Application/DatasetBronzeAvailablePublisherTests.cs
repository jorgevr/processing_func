using DatasetProcessingFunction.Application.Interfaces;
using DatasetProcessingFunction.Application.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DatasetProcessingFunction.UnitTests.Application;

public sealed class DatasetBronzeAvailablePublisherTests
{
    [Fact]
    public async Task Handle_PublishesDatasetBronzeAvailableEvent()
    {
        var publisherMock = new Mock<IEventPublisher>();
        string? capturedEventType = null;
        object? capturedPayload = null;
        string? capturedCorrelationId = null;

        publisherMock.Setup(p => p.PublishAsync(
                It.IsAny<string>(), It.IsAny<object>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, object, string, CancellationToken>((et, pl, cid, _) =>
            {
                capturedEventType = et;
                capturedPayload = pl;
                capturedCorrelationId = cid;
            })
            .Returns(Task.CompletedTask);

        var sut = new DatasetBronzeAvailablePublisher(
            publisherMock.Object,
            NullLogger<DatasetBronzeAvailablePublisher>.Instance);

        var notification = new DatasetBronzeAvailableNotification(
            "vendor-abc-20260315-001",
            5,
            new Uri("https://onelake.dfs.fabric.microsoft.com/bronze/data.parquet"),
            "v2",
            "00-abc123-def456-01",
            DateTimeOffset.UtcNow);

        await sut.Handle(notification, CancellationToken.None);

        publisherMock.Verify(p => p.PublishAsync(
            It.IsAny<string>(), It.IsAny<object>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        capturedEventType.Should().Be("dataset.bronze.available");
        capturedCorrelationId.Should().Be("00-abc123-def456-01");
    }

    [Fact]
    public void Constructor_NullPublisher_Throws()
    {
        var act = () => new DatasetBronzeAvailablePublisher(
            null!,
            NullLogger<DatasetBronzeAvailablePublisher>.Instance);

        act.Should().Throw<ArgumentNullException>().WithParameterName("publisher");
    }
}
