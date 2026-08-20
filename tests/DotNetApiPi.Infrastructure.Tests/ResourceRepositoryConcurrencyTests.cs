using DotNetApiPi.Application.Common.Exceptions;
using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.ValueObjects;
using DotNetApiPi.Infrastructure.Persistence;
using DotNetApiPi.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace DotNetApiPi.Infrastructure.Tests;

/// <summary>
/// Exercises the EF Core optimistic-concurrency guard: the aggregate's
/// <see cref="Resource.Version"/> is a concurrency token, so a save based on
/// a stale view fails with <see cref="DbUpdateConcurrencyException"/>, which
/// the repository translates into <see cref="ResourceConcurrencyException"/>
/// (HTTP 412 in the presentation layer).
/// </summary>
public sealed class ResourceRepositoryConcurrencyTests
{
    [Fact]
    public async Task Save_StaleAggregate_ThrowsResourceConcurrencyException()
    {
        await using var db = new InMemoryDatabase(new RecordingDomainEventDispatcher());

        // Writer 0 creates the resource (the store now holds version 0).
        var original = Resource.Create(new ResourceName("Shared Resource"));
        var creatingRepository = new ResourceRepository(db.Context);
        await creatingRepository.AddAsync(original);
        await creatingRepository.SaveChangesAsync();

        // Two "clients" load the same aggregate in separate units of work.
        await using var contextA = db.NewContext();
        var repositoryA = new ResourceRepository(contextA);
        var aggregateA = (await repositoryA.GetByIdAsync(original.Id))!;

        await using var contextB = db.NewContext();
        var repositoryB = new ResourceRepository(contextB);
        var aggregateB = (await repositoryB.GetByIdAsync(original.Id))!;

        Assert.Equal(0, aggregateA.Version);
        Assert.Equal(0, aggregateB.Version);

        // Client B commits a rename first: the store advances to version 1.
        aggregateB.Rename(new ResourceName("From B"));
        await repositoryB.SaveChangesAsync();

        // Client A is now stale: its write must be rejected by the
        // concurrency token instead of silently overwriting B's change.
        aggregateA.Rename(new ResourceName("From A"));
        var exception = await Assert.ThrowsAsync<ResourceConcurrencyException>(
            () => repositoryA.SaveChangesAsync());

        Assert.Equal(original.Id, exception.ResourceId);
        // The persistence failure is preserved for diagnosis.
        Assert.IsType<DbUpdateConcurrencyException>(exception.InnerException);

        // B's write survived: the store still holds B's name at version 1.
        await using var fresh = db.NewContext();
        var loaded = (await fresh.Resources.FindAsync(original.Id))!;
        Assert.Equal("From B", loaded.Name.Value);
        Assert.Equal(1, loaded.Version);
    }

    [Fact]
    public async Task Save_MatchingVersion_Succeeds_AndPersistsTheBumpedVersion()
    {
        await using var db = new InMemoryDatabase(new RecordingDomainEventDispatcher());

        var resource = Resource.Create(new ResourceName("Contended"));
        var repository = new ResourceRepository(db.Context);
        await repository.AddAsync(resource);
        await repository.SaveChangesAsync();

        // A fresh unit of work loads the aggregate and renames it: the
        // version filter matches, so the write succeeds and persists the
        // bumped version.
        await using var context = db.NewContext();
        var freshRepository = new ResourceRepository(context);
        var aggregate = (await freshRepository.GetByIdAsync(resource.Id))!;
        aggregate.Rename(new ResourceName("Contended v1"));
        await freshRepository.SaveChangesAsync();

        await using var verifier = db.NewContext();
        var loaded = (await verifier.Resources.FindAsync(resource.Id))!;
        Assert.Equal("Contended v1", loaded.Name.Value);
        Assert.Equal(1, loaded.Version);
    }

    [Fact]
    public async Task RoundTrip_PersistsAndRestoresTheVersion()
    {
        await using var db = new InMemoryDatabase(new RecordingDomainEventDispatcher());
        var repository = new ResourceRepository(db.Context);

        var resource = Resource.Create(
            new ResourceName("Versioned"),
            "A description",
            [new ResourceTag("round-trip")]);

        await repository.AddAsync(resource);
        await repository.SaveChangesAsync(); // version 0 persisted

        resource.Rename(new ResourceName("Versioned v1"));
        resource.SetDescription("Second description");
        await repository.SaveChangesAsync(); // version 2 persisted

        // Rehydration through the context restores the exact version.
        await using var fresh = db.NewContext();
        var loaded = (await fresh.Resources.FindAsync(resource.Id))!;

        Assert.Equal(2, loaded.Version);
        Assert.Equal("Versioned v1", loaded.Name.Value);
    }
}
