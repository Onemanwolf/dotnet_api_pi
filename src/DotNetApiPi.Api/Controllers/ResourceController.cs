using DotNetApiPi.Api.Dtos;
using DotNetApiPi.Application.Commands;
using DotNetApiPi.Application.Common;
using DotNetApiPi.Application.Dtos;
using DotNetApiPi.Application.Queries;
using Microsoft.AspNetCore.Mvc;

namespace DotNetApiPi.Api.Controllers;

/// <summary>
/// Exposes HTTP endpoints for the resource aggregate. Each action delegates
/// to an application layer command or query handler, keeping the presentation
/// layer free of business logic.
/// </summary>
[ApiController]
[Route("api/resources")]
[Produces("application/json")]
public sealed class ResourceController : ControllerBase
{
    private readonly IQueryHandler<GetAllResourcesQuery, IReadOnlyList<ResourceDto>> _getAllResourcesHandler;
    private readonly IQueryHandler<GetResourceByIdQuery, ResourceDto> _getResourceByIdHandler;
    private readonly ICommandHandler<CreateResourceCommand, ResourceDto> _createResourceHandler;
    private readonly ICommandHandler<UpdateResourceCommand, ResourceDto> _updateResourceHandler;
    private readonly ICommandHandler<ActivateResourceCommand, ResourceDto> _activateResourceHandler;
    private readonly ICommandHandler<ArchiveResourceCommand, ResourceDto> _archiveResourceHandler;
    private readonly ICommandHandler<DeleteResourceCommand, Unit> _deleteResourceHandler;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceController"/> class.
    /// </summary>
    /// <param name="getAllResourcesHandler">The handler for listing all resources.</param>
    /// <param name="getResourceByIdHandler">The handler for retrieving a resource by identifier.</param>
    /// <param name="createResourceHandler">The handler for creating a resource.</param>
    /// <param name="updateResourceHandler">The handler for updating a resource.</param>
    /// <param name="activateResourceHandler">The handler for activating a resource.</param>
    /// <param name="archiveResourceHandler">The handler for archiving a resource.</param>
    /// <param name="deleteResourceHandler">The handler for deleting a resource.</param>
    public ResourceController(
        IQueryHandler<GetAllResourcesQuery, IReadOnlyList<ResourceDto>> getAllResourcesHandler,
        IQueryHandler<GetResourceByIdQuery, ResourceDto> getResourceByIdHandler,
        ICommandHandler<CreateResourceCommand, ResourceDto> createResourceHandler,
        ICommandHandler<UpdateResourceCommand, ResourceDto> updateResourceHandler,
        ICommandHandler<ActivateResourceCommand, ResourceDto> activateResourceHandler,
        ICommandHandler<ArchiveResourceCommand, ResourceDto> archiveResourceHandler,
        ICommandHandler<DeleteResourceCommand, Unit> deleteResourceHandler)
    {
        _getAllResourcesHandler =
            getAllResourcesHandler ?? throw new ArgumentNullException(nameof(getAllResourcesHandler));
        _getResourceByIdHandler =
            getResourceByIdHandler ?? throw new ArgumentNullException(nameof(getResourceByIdHandler));
        _createResourceHandler =
            createResourceHandler ?? throw new ArgumentNullException(nameof(createResourceHandler));
        _updateResourceHandler =
            updateResourceHandler ?? throw new ArgumentNullException(nameof(updateResourceHandler));
        _activateResourceHandler =
            activateResourceHandler ?? throw new ArgumentNullException(nameof(activateResourceHandler));
        _archiveResourceHandler =
            archiveResourceHandler ?? throw new ArgumentNullException(nameof(archiveResourceHandler));
        _deleteResourceHandler =
            deleteResourceHandler ?? throw new ArgumentNullException(nameof(deleteResourceHandler));
    }

    /// <summary>
    /// Lists all resources.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ResourceDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var resources =
            await _getAllResourcesHandler.HandleAsync(new GetAllResourcesQuery(), cancellationToken);
        return Ok(resources);
    }

    /// <summary>
    /// Retrieves a resource by its identifier.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ResourceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var resource =
            await _getResourceByIdHandler.HandleAsync(new GetResourceByIdQuery(id), cancellationToken);
        return Ok(resource);
    }

    /// <summary>
    /// Creates a new resource.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ResourceDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(
        [FromBody] CreateResourceRequest request,
        CancellationToken cancellationToken)
    {
        var resource = await _createResourceHandler.HandleAsync(
            new CreateResourceCommand(request.Name, request.Description, request.Tags),
            cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = resource.Id }, resource);
    }

    /// <summary>
    /// Updates an existing resource.
    /// </summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ResourceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateResourceRequest request,
        CancellationToken cancellationToken)
    {
        var resource = await _updateResourceHandler.HandleAsync(
            new UpdateResourceCommand(id, request.Name, request.Description, request.Tags),
            cancellationToken);

        return Ok(resource);
    }

    /// <summary>
    /// Activates a resource that is currently in the draft state.
    /// </summary>
    [HttpPost("{id:guid}/activate")]
    [ProducesResponseType(typeof(ResourceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Activate(Guid id, CancellationToken cancellationToken)
    {
        var resource =
            await _activateResourceHandler.HandleAsync(new ActivateResourceCommand(id), cancellationToken);
        return Ok(resource);
    }

    /// <summary>
    /// Archives a resource that is currently active.
    /// </summary>
    [HttpPost("{id:guid}/archive")]
    [ProducesResponseType(typeof(ResourceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Archive(Guid id, CancellationToken cancellationToken)
    {
        var resource =
            await _archiveResourceHandler.HandleAsync(new ArchiveResourceCommand(id), cancellationToken);
        return Ok(resource);
    }

    /// <summary>
    /// Deletes a resource.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _deleteResourceHandler.HandleAsync(new DeleteResourceCommand(id), cancellationToken);
        return NoContent();
    }
}
