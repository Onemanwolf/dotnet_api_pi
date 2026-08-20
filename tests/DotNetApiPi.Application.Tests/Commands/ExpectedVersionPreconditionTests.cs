using DotNetApiPi.Application.Commands;
using DotNetApiPi.Application.Common;
using DotNetApiPi.Application.Common.Exceptions;
using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.Repositories;
using Moq;

namespace DotNetApiPi.Application.Tests.Commands;

/// <summary>
/// Exercises the application-layer optimistic-concurrency precondition
/// (<see cref="ConcurrencyPreconditions.EnsureMatches"/>) inside every
/// mutating command handler: a stale <c>If-Match</c>-derived version is
/// rejected with <see cref="ResourceConcurrencyException"/> before any
/// mutation is applied, while a matching (or absent) version proceeds.
/// </summary>
public sealed class ExpectedVersionPreconditionTests
{
    /// <summary>
    /// A draft resource at version 0 (Create only — no mutators have run).
    /// </summary>
    private static Resource DraftAtVersionZero()
        => ResourceFactory.Draft("Contended Resource");

    /// <summary>
    /// An active resource at version 1 (Create + Activate).
    /// </summary>
    private static Resource ActiveAtVersionOne()
        => ResourceFactory.Active("Contended Resource");

    [Fact]
    public async Task Update_StaleExpectedVersion_ThrowsBeforeAnyMutation()
    {
        var resource = DraftAtVersionZero();
        var repository = new Mock<IResourceRepository>();
        repository
            .Setup(r => r.GetByIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resource);
        var handler = new UpdateResourceCommandHandler(repository.Object);

        // The client still believes the resource is at version 3.
        await Assert.ThrowsAsync<ResourceConcurrencyException>(
            () => handler.HandleAsync(
                new UpdateResourceCommand(resource.Id, "New Name", null, null, 3)));

        // Nothing was mutated and nothing was persisted.
        Assert.Equal("Contended Resource", resource.Name.Value);
        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_MatchingExpectedVersion_AppliesTheChange()
    {
        var resource = DraftAtVersionZero();
        var repository = new Mock<IResourceRepository>();
        repository
            .Setup(r => r.GetByIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resource);
        var handler = new UpdateResourceCommandHandler(repository.Object);

        var dto = await handler.HandleAsync(
            new UpdateResourceCommand(resource.Id, "New Name", null, null, 0));

        Assert.Equal("New Name", resource.Name.Value);
        Assert.Equal(1, resource.Version);
        Assert.Equal(1, dto.Version);
        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_NoExpectedVersion_SkipsThePrecondition()
    {
        var resource = DraftAtVersionZero();
        var repository = new Mock<IResourceRepository>();
        repository
            .Setup(r => r.GetByIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resource);
        var handler = new UpdateResourceCommandHandler(repository.Object);

        // ExpectedVersion null (e.g. If-Match: *) skips the version check.
        var dto = await handler.HandleAsync(
            new UpdateResourceCommand(resource.Id, "New Name", null, null, null));

        Assert.Equal("New Name", resource.Name.Value);
        Assert.Equal(1, dto.Version);
    }

    [Fact]
    public async Task Activate_StaleExpectedVersion_ThrowsBeforeActivation()
    {
        var resource = DraftAtVersionZero();
        var repository = new Mock<IResourceRepository>();
        repository
            .Setup(r => r.GetByIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resource);
        var handler = new ActivateResourceCommandHandler(repository.Object);

        await Assert.ThrowsAsync<ResourceConcurrencyException>(
            () => handler.HandleAsync(new ActivateResourceCommand(resource.Id, 7)));

        Assert.Equal(Domain.Enums.ResourceStatus.Draft, resource.Status);
        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Activate_MatchingExpectedVersion_Activates()
    {
        var resource = DraftAtVersionZero();
        var repository = new Mock<IResourceRepository>();
        repository
            .Setup(r => r.GetByIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resource);
        var handler = new ActivateResourceCommandHandler(repository.Object);

        var dto = await handler.HandleAsync(new ActivateResourceCommand(resource.Id, 0));

        Assert.Equal(Domain.Enums.ResourceStatus.Active, resource.Status);
        Assert.Equal(1, dto.Version);
        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Archive_StaleExpectedVersion_ThrowsBeforeArchiving()
    {
        // An active resource sits at version 1 (Create + Activate).
        var resource = ActiveAtVersionOne();
        var repository = new Mock<IResourceRepository>();
        repository
            .Setup(r => r.GetByIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resource);
        var handler = new ArchiveResourceCommandHandler(repository.Object);

        await Assert.ThrowsAsync<ResourceConcurrencyException>(
            () => handler.HandleAsync(new ArchiveResourceCommand(resource.Id, 0)));

        Assert.Equal(Domain.Enums.ResourceStatus.Active, resource.Status);
        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Archive_MatchingExpectedVersion_Archives()
    {
        var resource = ActiveAtVersionOne();
        var repository = new Mock<IResourceRepository>();
        repository
            .Setup(r => r.GetByIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resource);
        var handler = new ArchiveResourceCommandHandler(repository.Object);

        var dto = await handler.HandleAsync(new ArchiveResourceCommand(resource.Id, 1));

        Assert.Equal(Domain.Enums.ResourceStatus.Archived, resource.Status);
        Assert.Equal(2, dto.Version);
        repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_StaleExpectedVersion_ThrowsBeforeRemoval()
    {
        var resource = DraftAtVersionZero();
        var repository = new Mock<IResourceRepository>();
        repository
            .Setup(r => r.GetByIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resource);
        var handler = new DeleteResourceCommandHandler(repository.Object);

        var exception = await Assert.ThrowsAsync<ResourceConcurrencyException>(
            () => handler.HandleAsync(new DeleteResourceCommand(resource.Id, 9)));

        Assert.Equal(resource.Id, exception.ResourceId);
        repository.Verify(r => r.RemoveAsync(resource, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_MatchingExpectedVersion_Removes()
    {
        var resource = DraftAtVersionZero();
        var repository = new Mock<IResourceRepository>();
        repository
            .Setup(r => r.GetByIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resource);
        var handler = new DeleteResourceCommandHandler(repository.Object);

        await handler.HandleAsync(new DeleteResourceCommand(resource.Id, 0));

        repository.Verify(r => r.RemoveAsync(resource, It.IsAny<CancellationToken>()), Times.Once);
    }
}
