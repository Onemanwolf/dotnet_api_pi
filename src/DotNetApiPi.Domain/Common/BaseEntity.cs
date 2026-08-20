namespace DotNetApiPi.Domain.Common;

/// <summary>
/// Represents the identity of an entity within a bounded context.
/// </summary>
/// <typeparam name="TId">The type used to uniquely identify the entity.</typeparam>
public abstract class BaseEntity<TId>
    where TId : notnull
{
    /// <summary>
    /// Gets or sets the unique identity of the entity.
    /// Set by the persistence layer or a derived constructor; never assigned by application code.
    /// </summary>
    public TId Id { get; protected set; } = default!;
}
