using DotNetApiPi.Domain.Exceptions;
using DotNetApiPi.Domain.ValueObjects;

namespace DotNetApiPi.Domain.Tests.ValueObjects;

/// <summary>
/// Tests for the <see cref="ResourceName"/> value object.
/// </summary>
public sealed class ResourceNameTests
{
    [Theory]
    [InlineData("  ")]
    [InlineData("\t\n")]
    public void Ctor_BlankValue_ThrowsDomainInputException(string value)
    {
        Assert.Throws<DomainInputException>(() => new ResourceName(value));
    }

    [Fact]
    public void Ctor_ValidValue_TrimsSurroundingWhitespace()
    {
        var name = new ResourceName("  padded name  ");

        Assert.Equal("padded name", name.Value);
    }

    [Fact]
    public void Ctor_ValueAtMaxLength_IsAccepted()
    {
        var value = new string('a', ResourceName.MaxLength);

        var name = new ResourceName(value);

        Assert.Equal(value, name.Value);
    }

    [Fact]
    public void Ctor_ValueOverMaxLength_ThrowsDomainInputException()
    {
        var value = new string('a', ResourceName.MaxLength + 1);

        var exception = Assert.Throws<DomainInputException>(
            () => new ResourceName(value));

        Assert.Contains(ResourceName.MaxLength.ToString(), exception.Message);
    }

    [Fact]
    public void Equals_ValueBased()
    {
        var first = new ResourceName("Same Name");
        var second = new ResourceName("Same Name");
        var different = new ResourceName("Other Name");

        Assert.True(first.Equals(second));
        Assert.False(first.Equals(different));
    }
}
