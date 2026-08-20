namespace DotNetApiPi.Infrastructure;

/// <summary>
/// Prepares the selected persistence provider before the application handles
/// traffic (schema creation, index creation, connectivity checks, ...).
/// <para>
/// The composition root (<c>DotNetApiPi.Api</c>) depends only on this
/// abstraction, so provider-specific details such as EF Core's
/// <c>EnsureCreated</c> stay inside the infrastructure layer.
/// </para>
/// </summary>
public interface IInfrastructureInitializer
{
    /// <summary>
    /// Performs any one-time preparation required by the persistence
    /// provider. Implementations must be idempotent — this method runs on
    /// every application start.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
