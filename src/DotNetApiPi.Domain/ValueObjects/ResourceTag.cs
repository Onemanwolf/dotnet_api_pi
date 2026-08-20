using System.Text;
using DotNetApiPi.Domain.Exceptions;

namespace DotNetApiPi.Domain.ValueObjects;

/// <summary>
/// Value object representing a single tag attached to a
/// <see cref="Entities.Resource"/>. A value object has no identity and is
/// compared by its value, not by reference.
/// </summary>
public sealed class ResourceTag : IEquatable<ResourceTag>
{
    /// <summary>
    /// The maximum number of characters a tag may contain after normalization.
    /// Together with <c>Resource.MaxTagCount</c> this keeps the serialized tag
    /// blob well within the persistence layer's column cap.
    /// </summary>
    public const int MaxLength = 64;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceTag"/> class.
    /// </summary>
    /// <param name="value">The raw tag value. Whitespace is normalized.</param>
    /// <exception cref="DomainInputException">
    /// Thrown when the value is null, empty, whitespace only, or exceeds
    /// <see cref="MaxLength"/> characters.
    /// </exception>
    public ResourceTag(string value)
    {
        var normalized = Sanitize(value);

        if (normalized.Length == 0)
        {
            throw new DomainInputException(
                "A resource tag must not be empty or whitespace only.");
        }

        if (normalized.Length > MaxLength)
        {
            throw new DomainInputException(
                $"A resource tag must not exceed {MaxLength} characters.");
        }

        Value = normalized;
    }

    /// <summary>
    /// Gets the normalized tag value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Determines whether the specified object is equal to this tag.
    /// </summary>
    public bool Equals(ResourceTag? other)
    {
        if (other is null)
        {
            return false;
        }

        return string.Equals(Value, other.Value, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ResourceTag);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    /// <summary>
    /// Determines whether two tags are equal.
    /// </summary>
    public static bool operator ==(ResourceTag? left, ResourceTag? right) =>
        left?.Equals(right) ?? right is null;

    /// <summary>
    /// Determines whether two tags are not equal.
    /// </summary>
    public static bool operator !=(ResourceTag? left, ResourceTag? right) =>
        !(left == right);

    /// <summary>
    /// Normalizes a raw tag into a lowercase token with whitespace removed.
    /// </summary>
    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
