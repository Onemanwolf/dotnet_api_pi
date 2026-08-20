namespace DotNetApiPi.Application.Common.Exceptions;

/// <summary>
/// Thrown when a requested resource cannot be found. The presentation layer
/// maps this exception to an HTTP 404 (Not Found) response.
/// </summary>
public sealed class ResourceNotFoundException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceNotFoundException"/> class.
    /// </summary>
    /// <param name="id">The identity of the resource that was not found.</param>
    public ResourceNotFoundException(Guid id)
        : base($"The resource with the identity '{id}' was not found.")
    {
        Id = id;
    }

    /// <summary>
    /// Gets the identity of the resource that was not found.
    /// </summary>
    public Guid Id { get; }
}
