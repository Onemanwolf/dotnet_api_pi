using DotNetApiPi.Domain.Exceptions;

namespace DotNetApiPi.Domain.ValueObjects;

/// <summary>
/// A value object representing the name of a resource.
/// Encapsulates the rules that a name must not be null, empty or whitespace,
/// and must not exceed <see cref="MaxLength"/> characters (matching the
/// persistence layer's column cap so that invariants can never be violated
/// at the store level).
/// </summary>
public sealed class ResourceName : ValueObject
{
    /// <summary>
    /// The maximum number of characters a resource name may contain.
    /// Mirrors the EF Core <c>HasMaxLength(256)</c> configuration so that the
    /// domain rejects over-length input before it ever reaches persistence.
    /// </summary>
    public const int MaxLength = 256;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceName"/> class.
    /// </summary>
    /// <param name="value">The raw name string.</param>
    /// <exception cref="DomainInputException">
    /// Thrown when the name is null, empty, or exceeds <see cref="MaxLength"/>.
    /// </exception>
    public ResourceName(string value)
    {
        Value = Validate(value);
    }

    /// <summary>
    /// Gets the validated name.
    /// </summary>
    public string Value { get; }

    /// <inheritdoc />
    protected override IEnumerable<object?> Members()
    {
        yield return Value;
    }

    /// <summary>
    /// Validates the raw name and returns the normalized value.
    /// </summary>
    private static string Validate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainInputException("A resource must have a name.");
        }

        var trimmed = value.Trim();

        if (trimmed.Length > MaxLength)
        {
            throw new DomainInputException(
                $"A resource name must not exceed {MaxLength} characters.");
        }

        return trimmed;
    }
}
