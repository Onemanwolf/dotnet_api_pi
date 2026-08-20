using DotNetApiPi.Application.Common;
using DotNetApiPi.Application.Common.Exceptions;
using DotNetApiPi.Domain.Repositories;

namespace DotNetApiPi.Application.Commands;

/// <summary>
/// Handles the <see cref="DeleteResourceCommand"/> by loading the aggregate
/// and removing it from the repository.
/// </summary>
public sealed class DeleteResourceCommandHandler : ICommandHandler<DeleteResourceCommand, Unit>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeleteResourceCommandHandler"/> class.
    /// </summary>
    /// <param name="repository">The resource repository.</param>
    public DeleteResourceCommandHandler(IResourceRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    private readonly IResourceRepository _repository;

    /// <inheritdoc />
    public async Task<Unit> HandleAsync(
        DeleteResourceCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var resource = await _repository.GetByIdAsync(
            command.Id,
            cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException(command.Id);

        // Optimistic concurrency (application layer): reject a request that
        // is based on a stale version before the aggregate is removed. Maps
        // to HTTP 412 via the exception-mapping middleware.
        ConcurrencyPreconditions.EnsureMatches(resource, command.ExpectedVersion);

        // Stage the deletion domain event on the aggregate so that it is
        // persisted (Mongo: outbox row inside the same unit of work) before
        // the document is removed.
        resource.Delete();

        await _repository.RemoveAsync(resource, cancellationToken).ConfigureAwait(false);
        await _repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Unit.Value;
    }
}
