namespace DotNetApiPi.Domain.ValueObjects;

/// <summary>
/// Base class for value objects. Value objects have no identity and are
/// defined entirely by their attributes. Equality is based on value,
/// not reference.
/// </summary>
public abstract class ValueObject
{
    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        if (obj is null || obj.GetType() != GetType())
        {
            return false;
        }

        var other = (ValueObject)obj;
        var equalMembers = Members()
            .SequenceEqual(other.Members());

        return equalMembers;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        // Note the parentheses: without them the expression would bind as
        // `(accumulator * 31 + member?.GetHashCode()) ?? 0` (the `+` operator
        // has higher precedence than `??`), which collapses the whole
        // accumulator to 0 for the remainder of the fold whenever a member is
        // null.
        return Members()
            .Aggregate(1, (accumulator, member) =>
                accumulator * 31 + (member?.GetHashCode() ?? 0));
    }

    /// <summary>
    /// Determines whether two value objects are equal by value.
    /// </summary>
    public static bool operator ==(ValueObject? left, ValueObject? right)
    {
        return Equals(left, right);
    }

    /// <summary>
    /// Determines whether two value objects are not equal by value.
    /// </summary>
    public static bool operator !=(ValueObject? left, ValueObject? right)
    {
        return !Equals(left, right);
    }

    /// <summary>
    /// Returns the values of the members that participate in equality.
    /// </summary>
    protected abstract IEnumerable<object?> Members();
}
