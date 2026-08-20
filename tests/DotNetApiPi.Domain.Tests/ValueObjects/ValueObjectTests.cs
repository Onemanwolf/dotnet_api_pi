using DotNetApiPi.Domain.ValueObjects;

namespace DotNetApiPi.Domain.Tests.ValueObjects;

/// <summary>
/// Tests for the shared <see cref="ValueObject"/> equality and hashing logic.
/// </summary>
public sealed class ValueObjectTests
{
    /// <summary>
    /// A minimal concrete value object with a nullable and a non-nullable
    /// member, used to exercise the hashing fold.
    /// </summary>
    private sealed class TestValueObject(string? a, int b) : ValueObject
    {
        /// <summary>
        /// Gets member A (may be null).
        /// </summary>
        public string? A { get; } = a;

        /// <summary>
        /// Gets member B.
        /// </summary>
        public int B { get; } = b;

        /// <inheritdoc />
        protected override IEnumerable<object?> Members()
        {
            yield return A;
            yield return B;
        }
    }

    [Fact]
    public void GetHashCode_DoesNotCollapseToZero_WhenAMemberIsNull()
    {
        // Regression test: without the parentheses around
        // `member?.GetHashCode() ?? 0` the whole fold collapses to 0 whenever
        // the first member is null (the `+` binds tighter than `??`).
        var hash = new TestValueObject(null, 1).GetHashCode();

        Assert.NotEqual(0, hash);
    }

    [Fact]
    public void GetHashCode_Differs_WhenNonNullMemberDiffers_WithNullFirstMember()
    {
        // Regression test for the same precedence bug: two value objects that
        // differ only in their second member (first member null) must not hash
        // identically, or dictionary lookups would degrade to O(n).
        var first = new TestValueObject(null, 1).GetHashCode();
        var second = new TestValueObject(null, 2).GetHashCode();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Equals_ValueBased_IgnoringReferenceIdentity()
    {
        var first = new TestValueObject("a", 1);
        var second = new TestValueObject("a", 1);
        var different = new TestValueObject("a", 2);

        Assert.True(first.Equals(second));
        Assert.False(first.Equals(different));
    }

    [Fact]
    public void Equals_TreatsNullMembersAsEqual()
    {
        var first = new TestValueObject(null, 1);
        var second = new TestValueObject(null, 1);

        Assert.True(first.Equals(second));
    }

    [Fact]
    public void Equals_ReturnsFalse_ForDifferentValueType()
    {
        var valueObject = new TestValueObject("a", 1);

        Assert.False(valueObject.Equals("a"));
    }
}
