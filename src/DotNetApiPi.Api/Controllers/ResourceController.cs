using DotNetApiPi.Api.Dtos;
using DotNetApiPi.Api.Results;
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
/// <para>
/// <b>Paging.</b> <c>GET /api/resources</c> is bounded: clients select a
/// 1-based <c>page</c> and a <c>pageSize</c> (default 20, maximum 100). The
/// response body stays a bare JSON array of <c>ResourceDto</c>; the paging
/// metadata is exposed through the <c>X-Total-Count</c> and
/// <c>X-Total-Pages</c> response headers. Out-of-range paging parameters are
/// rejected with a 400 problem+json document.
/// </para>
/// <para>
/// <b>Optimistic concurrency.</b> Single-resource responses carry an
/// <c>ETag: "&lt;version&gt;"</c> header (a strong validator mirroring the
/// <c>version</c> field in the body). Every mutating endpoint (PUT,
/// activate, archive, DELETE) <i>requires</i> an <c>If-Match</c> header
/// carrying that ETag: a missing header is rejected with
/// 428 Precondition Required, a stale version with 412 Precondition Failed
/// (thrown as <c>ResourceConcurrencyException</c> by the application layer
/// and mapped by the exception middleware).
/// </para>
/// </summary>
[ApiController]
[Route("api/resources")]
[Produces("application/json")]
public sealed class ResourceController : ControllerBase
{
    /// <summary>
    /// Stable base URI for this API's RFC 7807 problem documents (shared with
    /// <c>ExceptionHandlingMiddleware</c>, which owns the server-side halves
    /// of the same contract).
    /// </summary>
    private const string ProblemBaseUri = "https://dotnet-api-pi.example/errors";

    private readonly IQueryHandler<GetAllResourcesQuery, PagedResult<ResourceDto>> _getAllResourcesHandler;
    private readonly IQueryHandler<GetResourceByIdQuery, ResourceDto> _getResourceByIdHandler;
    private readonly ICommandHandler<CreateResourceCommand, ResourceDto> _createResourceHandler;
    private readonly ICommandHandler<UpdateResourceCommand, ResourceDto> _updateResourceHandler;
    private readonly ICommandHandler<ActivateResourceCommand, ResourceDto> _activateResourceHandler;
    private readonly ICommandHandler<ArchiveResourceCommand, ResourceDto> _archiveResourceHandler;
    private readonly ICommandHandler<DeleteResourceCommand, Unit> _deleteResourceHandler;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceController"/> class.
    /// </summary>
    /// <param name="getAllResourcesHandler">The handler for listing resources (paged).</param>
    /// <param name="getResourceByIdHandler">The handler for retrieving a resource by identifier.</param>
    /// <param name="createResourceHandler">The handler for creating a resource.</param>
    /// <param name="updateResourceHandler">The handler for updating a resource.</param>
    /// <param name="activateResourceHandler">The handler for activating a resource.</param>
    /// <param name="archiveResourceHandler">The handler for archiving a resource.</param>
    /// <param name="deleteResourceHandler">The handler for deleting a resource.</param>
    public ResourceController(
        IQueryHandler<GetAllResourcesQuery, PagedResult<ResourceDto>> getAllResourcesHandler,
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
    /// Lists one page of resources (ordered deterministically by identity).
    /// </summary>
    /// <param name="page">The 1-based page number (defaults to the first page).</param>
    /// <param name="pageSize">
    /// The maximum number of items per page (defaults to 20; at most 100).
    /// </param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ResourceDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetAll(
        [FromQuery(Name = "page")] int page = GetAllResourcesQuery.DefaultPage,
        [FromQuery(Name = "pageSize")] int pageSize = GetAllResourcesQuery.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize < 1 || pageSize > GetAllResourcesQuery.MaxPageSize)
        {
            return ProblemJson(
                StatusCodes.Status400BadRequest,
                $"{ProblemBaseUri}/bad-request",
                "Bad request",
                $"Paging parameters are out of range: page must be >= {GetAllResourcesQuery.DefaultPage} and pageSize between 1 and {GetAllResourcesQuery.MaxPageSize}.");
        }

        var result = await _getAllResourcesHandler
            .HandleAsync(new GetAllResourcesQuery(page, pageSize), cancellationToken);

        // The body stays a bare JSON array; the paging metadata is exposed
        // through headers (X-Total-Pages is derived from the total count).
        Response.Headers["X-Total-Count"] = result.TotalCount.ToString();
        Response.Headers["X-Total-Pages"] = result.TotalPages.ToString();

        return Ok(result.Items);
    }

    /// <summary>
    /// Retrieves a resource by its identifier. The response carries an
    /// <c>ETag: "&lt;version&gt;"</c> header for optimistic concurrency.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ResourceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var resource =
            await _getResourceByIdHandler.HandleAsync(new GetResourceByIdQuery(id), cancellationToken);

        // Strong validator: the quoted version mirrors the `version` field in
        // the body, so clients can read it either way.
        Response.Headers["ETag"] = $"\"{resource.Version}\"";
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
    /// Updates an existing resource. Requires the <c>If-Match</c> header.
    /// </summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ResourceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        [FromBody] UpdateResourceRequest request,
        CancellationToken cancellationToken)
    {
        var problem = RequireIfMatch(ifMatch, out int? expectedVersion);
        if (problem is not null)
        {
            return problem;
        }

        var resource = await _updateResourceHandler.HandleAsync(
            new UpdateResourceCommand(
                id,
                request.Name,
                request.Description,
                request.Tags,
                expectedVersion),
            cancellationToken);

        return Ok(resource);
    }

    /// <summary>
    /// Activates a resource that is currently in the draft state. Requires
    /// the <c>If-Match</c> header.
    /// </summary>
    [HttpPost("{id:guid}/activate")]
    [ProducesResponseType(typeof(ResourceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Activate(
        Guid id,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        CancellationToken cancellationToken)
    {
        var problem = RequireIfMatch(ifMatch, out int? expectedVersion);
        if (problem is not null)
        {
            return problem;
        }

        var resource = await _activateResourceHandler
            .HandleAsync(new ActivateResourceCommand(id, expectedVersion), cancellationToken);
        return Ok(resource);
    }

    /// <summary>
    /// Archives a resource that is currently active. Requires the
    /// <c>If-Match</c> header.
    /// </summary>
    [HttpPost("{id:guid}/archive")]
    [ProducesResponseType(typeof(ResourceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Archive(
        Guid id,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        CancellationToken cancellationToken)
    {
        var problem = RequireIfMatch(ifMatch, out int? expectedVersion);
        if (problem is not null)
        {
            return problem;
        }

        var resource = await _archiveResourceHandler
            .HandleAsync(new ArchiveResourceCommand(id, expectedVersion), cancellationToken);
        return Ok(resource);
    }

    /// <summary>
    /// Deletes a resource. Requires the <c>If-Match</c> header.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    public async Task<IActionResult> Delete(
        Guid id,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        CancellationToken cancellationToken)
    {
        var problem = RequireIfMatch(ifMatch, out int? expectedVersion);
        if (problem is not null)
        {
            return problem;
        }

        await _deleteResourceHandler
            .HandleAsync(new DeleteResourceCommand(id, expectedVersion), cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Enforces the <c>If-Match</c> precondition on a mutating request and
    /// parses the header into the expected aggregate version.
    /// </summary>
    /// <param name="ifMatch">The raw <c>If-Match</c> header value, if present.</param>
    /// <param name="expectedVersion">
    /// The parsed expected version, or <c>null</c> when the client asserted no
    /// version precondition (<c>If-Match: *</c>).
    /// </param>
    /// <returns>
    /// A 428 or 400 problem+json result when the request is rejected, or
    /// <c>null</c> when the request may proceed.
    /// </returns>
    private static IActionResult? RequireIfMatch(string? ifMatch, out int? expectedVersion)
    {
        // Every mutating endpoint requires If-Match: without it the client
        // asserts no knowledge of the current state, and silently overwriting
        // a concurrent writer is exactly the failure mode ETags exist to
        // prevent.
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            expectedVersion = null;
            return ProblemJson(
                StatusCodes.Status428PreconditionRequired,
                $"{ProblemBaseUri}/precondition-required",
                "Precondition required",
                "The If-Match header is required on all mutating requests and must carry the resource's ETag.");
        }

        // RFC 7212: the wildcard matches any current state — proceed without
        // a version check (the persistence layer's write-level guard still
        // applies for updates/deletes).
        if (ifMatch.Trim() == "*")
        {
            expectedVersion = null;
            return null;
        }

        // Strong entity tag: the quoted version, e.g. "\"3\"".
        var value = ifMatch.Trim().Trim('"');
        if (int.TryParse(value, out var version) && version >= 0)
        {
            expectedVersion = version;
            return null;
        }

        expectedVersion = null;
        return ProblemJson(
            StatusCodes.Status400BadRequest,
            $"{ProblemBaseUri}/bad-request",
            "Bad request",
            $"The If-Match header must be a strong entity tag of the form '\"<version>\"' or '*', but was '{ifMatch}'.");
    }

    /// <summary>
    /// Builds a problem+json result using the API's unified error contract
    /// (same document shape as the exception middleware and the
    /// model-binding error factory).
    /// </summary>
    private static ProblemJsonResult ProblemJson(
        int status,
        string type,
        string title,
        string detail)
    {
        return new ProblemJsonResult(new ProblemDetails
        {
            Status = status,
            Type = type,
            Title = title,
            Detail = detail
        });
    }
}
