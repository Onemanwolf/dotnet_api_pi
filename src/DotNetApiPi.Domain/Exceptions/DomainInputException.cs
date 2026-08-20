namespace DotNetApiPi.Domain.Exceptions;

/// <summary>
/// Represents an exception raised when client-supplied input violates a
/// domain invariant at the value-object or aggregate boundary — for example
/// an empty resource name, a value that exceeds the permitted length, or too
/// many tags.
/// <para>
/// This is deliberately a <em>sibling</em> of <see cref="DomainException"/>
/// (not a subclass): input-validation failures are the client's fault and are
/// mapped to HTTP 400 (Bad Request) by the presentation layer, whereas
/// <see cref="DomainException"/> signals a state-transition conflict (such as
/// activating an already archived resource) and is mapped to HTTP 409
/// (Conflict).
/// </para>
/// </summary>
public sealed class DomainInputException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DomainInputException"/>
    /// class.
    /// </summary>
    /// <param name="message">The message describing the invalid input.</param>
    public DomainInputException(string message)
        : base(message)
    {
    }
}
