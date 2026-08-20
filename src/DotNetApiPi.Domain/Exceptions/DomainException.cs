namespace DotNetApiPi.Domain.Exceptions;

/// <summary>
/// Represents an exception that arises from a violation of a domain invariant.
/// Thrown by entities and value objects when business rules are not satisfied.
/// </summary>
public sealed class DomainException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DomainException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the invariant violation.</param>
    public DomainException(string message)
        : base(message)
    {
    }
}
