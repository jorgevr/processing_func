using DatasetProcessingFunction.Domain.ValueObjects;
using FluentAssertions;

namespace DatasetProcessingFunction.UnitTests.Domain;

public sealed class DatasetIdTests
{
    [Fact]
    public void Constructor_ValidValue_SetsValue()
    {
        var id = new DatasetId("vendor-abc-20260315-001");
        id.Value.Should().Be("vendor-abc-20260315-001");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Constructor_NullOrEmpty_Throws(string? value)
    {
        var act = () => new DatasetId(value!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TwoIds_WithSameValue_AreEqual()
    {
        var a = new DatasetId("same-id");
        var b = new DatasetId("same-id");
        a.Should().Be(b);
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void TwoIds_WithDifferentValues_AreNotEqual()
    {
        var a = new DatasetId("id-1");
        var b = new DatasetId("id-2");
        a.Should().NotBe(b);
    }

    [Fact]
    public void ToString_ReturnsValue()
    {
        var id = new DatasetId("test-id");
        id.ToString().Should().Be("test-id");
    }
}
