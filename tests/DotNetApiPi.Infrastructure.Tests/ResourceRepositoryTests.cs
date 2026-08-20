using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.Enums;
using DotNetApiPi.Domain.Events;
using DotNetApiPi.Domain.ValueObjects;
using DotNetApiPi.Infrastructure.Persistence;
using DotNetApiPi.Infrastructure.Repositories;

namespace DotNetApiPi.Infrastructure.Tests;

/// <summary>
/// Exercises the EF Core unit of work (value conversions, save semantics and
/// domain event dispatch) against a real SQLite database.
/// </summary>
public sealed class ResourceRepositoryTests
{
    [Fact]
    public async Task Save_PersistsNewResource_AndDispatchesCreatedEvent()
    {
        await using var db = new InMemoryDatabase(new RecordingDomainEventDispatcher());
        var repository = new ResourceRepository(db.Context);

        var resource = Resource.Create(
            new ResourceName("Persisted Resource"),
            "A description",
            [new ResourceTag("Cloud"), new ResourceTag("storage")]);

        await repository.AddAsync(resource);
        await repository.SaveChangesAsync();

        // The created event was dispatched exactly once...
        var dispatched = db.Dispatcher.Dispatched.OfType<ResourceCreatedEvent>().ToList();
        Assert.Single(dispatched);
        Assert.Equal(resource.Id, dispatched[0].ResourceId);

        // ...and the aggregate was persisted and rehydrated faithfully.
        await using var fresh = db.NewContext();
        var loaded = await fresh.Resources.FindAsync(resource.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Persisted Resource", loaded!.Name.Value);
        Assert.Equal("A description", loaded.Description);
        Assert.Equal(ResourceStatus.Draft, loaded.Status);
        Assert.Equal(
            new[] { "cloud", "storage" },
            loaded.Tags.Select(static tag => tag.Value).ToArray());

        // ...and the pending events were cleared after dispatch.
        Assert.Empty(resource.DomainEvents);
    }

    [Fact]
    public async Task Save_UpdatedResource_PersistsChanges()
    {
        await using var db = new InMemoryDatabase(new RecordingDomainEventDispatcher());
        var repository = new ResourceRepository(db.Context);

        var resource = Resource.Create(new ResourceName("Original Name"));
        await repository.AddAsync(resource);
        await repository.SaveChangesAsync();

        resource.Rename(new ResourceName("Renamed Name"));
        resource.SetDescription("New description");
        resource.Activate();
        await repository.SaveChangesAsync();

        await using var fresh = db.NewContext();
        var loaded = (await fresh.Resources.FindAsync(resource.Id))!;

        Assert.Equal("Renamed Name", loaded.Name.Value);
        Assert.Equal("New description", loaded.Description);
        Assert.Equal(ResourceStatus.Active, loaded.Status);
    }

    [Fact]
    public async Task Save_RemovedResource_DeletesRow()
    {
        await using var db = new InMemoryDatabase(new RecordingDomainEventDispatcher());
        var repository = new ResourceRepository(db.Context);

        var resource = Resource.Create(new ResourceName("Doomed Resource"));
        await repository.AddAsync(resource);
        await repository.SaveChangesAsync();

        await repository.RemoveAsync(resource);
        var affected = await repository.SaveChangesAsync();

        Assert.Equal(1, affected);

        await using var fresh = db.NewContext();
        Assert.Null(await fresh.Resources.FindAsync(resource.Id));
    }

    [Fact]
    public async Task SaveChanges_WithoutPendingEvents_DoesNotRedispatch()
    {
        await using var db = new InMemoryDatabase(new RecordingDomainEventDispatcher());
        var repository = new ResourceRepository(db.Context);

        var resource = Resource.Create(new ResourceName("Once Only"));
        await repository.AddAsync(resource);
        await repository.SaveChangesAsync();
        Assert.Single(db.Dispatcher.Dispatched);

        // A second save (no new events staged) must not dispatch again.
        await repository.SaveChangesAsync();

        Assert.Single(db.Dispatcher.Dispatched);
    }

    [Fact]
    public async Task GetById_ReturnsNull_ForUnknownId()
    {
        await using var db = new InMemoryDatabase(new RecordingDomainEventDispatcher());
        var repository = new ResourceRepository(db.Context);

        var result = await repository.GetByIdAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPage_ReturnsFirstPage_OrderedById()
    {
        await using var db = new InMemoryDatabase(new RecordingDomainEventDispatcher());
        var repository = new ResourceRepository(db.Context);

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var ids = new[] { first, second, third }
            .OrderBy(static id => id)
            .ToArray();

        await db.Context.Resources.AddRangeAsync(
            CreateWithId(ids[0]),
            CreateWithId(ids[1]),
            CreateWithId(ids[2]));
        await db.Context.SaveChangesAsync();

        var (items, totalCount) = await repository.GetPageAsync(page: 1, pageSize: 2);

        Assert.Equal(3, totalCount);
        Assert.Equal(
            new[] { ids[0], ids[1] },
            items.Select(static resource => resource.Id).ToArray());

        // The second page carries the remainder — pages do not overlap.
        var (pageTwo, totalCountSecond) = await repository.GetPageAsync(page: 2, pageSize: 2);
        Assert.Equal(3, totalCountSecond);
        Assert.Equal([ids[2]], pageTwo.Select(static resource => resource.Id).ToArray());
    }

    [Fact]
    public async Task GetPage_PastTheEnd_ReturnsEmptyItems_WithFullCount()
    {
        await using var db = new InMemoryDatabase(new RecordingDomainEventDispatcher());
        var repository = new ResourceRepository(db.Context);

        await db.Context.Resources.AddAsync(CreateWithId(Guid.NewGuid()));
        await db.Context.SaveChangesAsync();

        var (items, totalCount) = await repository.GetPageAsync(page: 99, pageSize: 20);

        Assert.Empty(items);
        Assert.Equal(1, totalCount);
    }

    /// <summary>
    /// Creates a resource with a controlled identity by reconstituting it
    /// (internal, granted to this test assembly via InternalsVisibleTo).
    /// </summary>
    private static Resource CreateWithId(Guid id)
    {
        return Resource.Reconstitute(
            id,
            new ResourceName($"Resource {id}"),
            null,
            Domain.Enums.ResourceStatus.Draft,
            null,
            version: 0);
    }
}
