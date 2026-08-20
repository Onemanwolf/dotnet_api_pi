using DotNetApiPi.Domain.Exceptions;
using DotNetApiPi.Domain.ValueObjects;

namespace DotNetApiPi.Domain.Tests.ValueObjects;

/// <summary>
/// Tests for the <see cref="ResourceTag"/> value object.
/// </summary>
public sealed class ResourceTagTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_BlankValue_ThrowsDomainInputException(string value)
    {
        Assert.Throws<DomainInputException>(() => new ResourceTag(value));
    }

    [Fact]
    public void Ctor_ValidValue_NormalizesToLowercaseWithoutWhitespace()
    {
        var tag = new ResourceTag("  Cloud  Storage  ");

        Assert.Equal("cloudstorage", tag.Value);
    }

    [Fact]
    public void Ctor_ValueAtMaxLength_IsAccepted()
    {
        var value = new string('x', ResourceTag.MaxLength);

        var tag = new ResourceTag(value);

        Assert.Equal(value, tag.Value);
    }

    [Fact]
    public void Ctor_ValueOverMaxLength_ThrowsDomainInputException()
    {
        var value = new string('x', ResourceTag.MaxLength + 1);

        var exception = Assert.Throws<DomainInputException>(
            () => new ResourceTag(value));

        Assert.Contains(ResourceTag.MaxLength.ToString(), exception.Message);
    }

    [Fact]
    public void Equals_ValueBased_AndCaseInsensitive()
    {
        // Both normalize to the same lowercase token.
        var first = new ResourceTag("Cloud");
        var second = new ResourceTag("cloud");
        var different = new ResourceTag("storage");

        Assert.True(first.Equals(second));
        Assert.False(first.Equals(different));
        Assert.Equal(first, second);
        Assert.NotEqual(first, different);
    }
}
