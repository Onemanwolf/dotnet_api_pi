using DotNetApiPi.Application.Commands;
using DotNetApiPi.Application.Common;
using DotNetApiPi.Application.Dtos;
using DotNetApiPi.Application.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetApiPi.Application;

/// <summary>
/// Registers the application layer's services (use case handlers) into the
/// dependency injection container. Concrete infrastructure dependencies such
/// as the resource repository and domain event dispatcher are provided by the
/// infrastructure layer.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds the application layer's services to the service collection.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <returns>The same service collection, to enable method chaining.</returns>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<ICommandHandler<CreateResourceCommand, ResourceDto>, CreateResourceCommandHandler>();
        services.AddScoped<ICommandHandler<UpdateResourceCommand, ResourceDto>, UpdateResourceCommandHandler>();
        services.AddScoped<ICommandHandler<ActivateResourceCommand, ResourceDto>, ActivateResourceCommandHandler>();
        services.AddScoped<ICommandHandler<ArchiveResourceCommand, ResourceDto>, ArchiveResourceCommandHandler>();
        services.AddScoped<ICommandHandler<DeleteResourceCommand, Unit>, DeleteResourceCommandHandler>();

        services.AddScoped<IQueryHandler<GetResourceByIdQuery, ResourceDto>, GetResourceByIdQueryHandler>();
        services.AddScoped<IQueryHandler<GetAllResourcesQuery, IReadOnlyList<ResourceDto>>, GetAllResourcesQueryHandler>();

        return services;
    }
}
