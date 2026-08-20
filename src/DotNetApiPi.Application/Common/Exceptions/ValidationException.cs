namespace DotNetApiPi.Application.Common.Exceptions;

/// <summary>
/// Thrown when a command or query fails validation. The presentation layer
/// maps this exception to an HTTP 400 (Bad Request) response and includes
/// the individual field errors in the response body.
/// </summary>
public sealed class ValidationException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ValidationException"/> class
    /// from a set of validation error messages.
    /// </summary>
    /// <param name="errors">The validation error messages.</param>
    public ValidationException(IEnumerable<string> errors)
        : base("One or more validation errors have occurred.")
    {
        Errors = errors.ToList();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ValidationException"/> class
    /// from a single validation error message.
    /// </summary>
    /// <param name="error">The validation error message.</param>
    public ValidationException(string error)
        : this(new[] { error })
    {
    }

    /// <summary>
    /// Gets the validation error messages.
    /// </summary>
    public IReadOnlyList<string> Errors { get; }
}
