using DotNetApiPi.Domain.Common;
using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.Enums;
using DotNetApiPi.Domain.Events;
using DotNetApiPi.Domain.Exceptions;
using DotNetApiPi.Domain.ValueObjects;

namespace DotNetApiPi.Domain.Tests.Entities;

/// <summary>
/// Tests for the <see cref="Resource"/> aggregate: creation, lifecycle
/// transitions, length and count invariants.
/// </summary>
public sealed class ResourceTests
{
    [Fact]
    public void Create_ReturnsDraftResource_WithDomainEvent()
    {
        var resource = Resource.Create(new ResourceName("My Resource"));

        Assert.Equal(ResourceStatus.Draft, resource.Status);
        Assert.NotEmpty(resource.DomainEvents);
        Assert.Contains(
            resource.DomainEvents,
            static e => e is ResourceCreatedEvent);
    }

    [Fact]
    public void Create_WithFixedTimeProvider_UsesThatTimestamp()
    {
        var fixedTime = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var resource = Resource.Create(
            new ResourceName("My Resource"),
            timeProvider: new FixedTimeProvider(fixedTime));

        var @event = Assert.IsType<ResourceCreatedEvent>(
            resource.DomainEvents.Single());

        Assert.Equal(fixedTime.UtcDateTime, @event.OccurredOn);
    }

    /// <summary>
    /// A minimal <see cref="TimeProvider"/> pinned to a fixed UTC instant, so
    /// the created-event timestamp can be asserted deterministically.
    /// </summary>
    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    [Fact]
    public void Create_DescriptionOverMaxLength_ThrowsDomainInputException()
    {
        var description = new string('d', Resource.MaxDescriptionLength + 1);

        Assert.Throws<DomainInputException>(
            () => Resource.Create(new ResourceName("My Resource"), description));
    }

    [Fact]
    public void Create_MoreThanMaxTagCount_ThrowsDomainInputException()
    {
        var tags = Enumerable.Range(0, Resource.MaxTagCount + 1)
            .Select(static i => new ResourceTag($"tag-{i}"));

        Assert.Throws<DomainInputException>(
            () => Resource.Create(new ResourceName("My Resource"), tags: tags));
    }

    [Fact]
    public void Create_TagCollectionWithinLimit_IsAccepted()
    {
        var tags = Enumerable.Range(0, Resource.MaxTagCount)
            .Select(static i => new ResourceTag($"tag-{i}"));

        var resource = Resource.Create(new ResourceName("My Resource"), tags: tags);

        Assert.Equal(Resource.MaxTagCount, resource.Tags.Length);
    }

    [Fact]
    public void Rename_SetsNewName()
    {
        var resource = Resource.Create(new ResourceName("Old Name"));

        resource.Rename(new ResourceName("New Name"));

        Assert.Equal("New Name", resource.Name.Value);
    }

    [Fact]
    public void SetDescription_OverMaxLength_ThrowsDomainInputException()
    {
        var resource = Resource.Create(new ResourceName("My Resource"));

        var description = new string('d', Resource.MaxDescriptionLength + 1);

        Assert.Throws<DomainInputException>(() => resource.SetDescription(description));
    }

    [Fact]
    public void SetDescription_Null_ClearsDescription()
    {
        var resource = Resource.Create(new ResourceName("My Resource"), "A description");

        resource.SetDescription(null);

        Assert.Null(resource.Description);
    }

    [Fact]
    public void Activate_FromDraft_SetsActive()
    {
        var resource = Resource.Create(new ResourceName("My Resource"));

        resource.Activate();

        Assert.Equal(ResourceStatus.Active, resource.Status);
    }

    [Fact]
    public void Activate_FromActive_ThrowsDomainException()
    {
        var resource = Resource.Create(new ResourceName("My Resource"));
        resource.Activate();

        var exception = Assert.Throws<DomainException>(() => resource.Activate());

        Assert.Contains("Draft", exception.Message);
    }

    [Fact]
    public void Activate_FromArchived_ThrowsDomainException()
    {
        var resource = Resource.Create(new ResourceName("My Resource"));
        resource.Activate();
        resource.Archive();

        var exception = Assert.Throws<DomainException>(() => resource.Activate());

        Assert.Contains("Archived", exception.Message);
    }

    [Fact]
    public void Archive_FromActive_SetsArchived()
    {
        var resource = Resource.Create(new ResourceName("My Resource"));
        resource.Activate();

        resource.Archive();

        Assert.Equal(ResourceStatus.Archived, resource.Status);
    }

    [Fact]
    public void Archive_FromDraft_ThrowsDomainException()
    {
        var resource = Resource.Create(new ResourceName("My Resource"));

        var exception = Assert.Throws<DomainException>(() => resource.Archive());

        Assert.Contains("Draft", exception.Message);
    }

    [Fact]
    public void AddTag_ExistingTag_DoesNotDuplicate()
    {
        var resource = Resource.Create(
            new ResourceName("My Resource"),
            tags: [new ResourceTag("cloud")]);

        resource.AddTag(new ResourceTag("CLOUD")); // normalizes to "cloud"

        Assert.Single(resource.Tags);
    }

    #region Archived resources are immutable (terminal state)

    /// <summary>
    /// Builds a resource in the terminal (archived) state: Draft → Active →
    /// Archived.
    /// </summary>
    private static Resource CreateArchived()
    {
        var resource = Resource.Create(new ResourceName("My Resource"), tags: [new ResourceTag("cloud")]);
        resource.Activate();
        resource.Archive();
        return resource;
    }

    [Fact]
    public void Rename_OnArchivedResource_ThrowsDomainException()
    {
        var resource = CreateArchived();

        var exception = Assert.Throws<DomainException>(
            () => resource.Rename(new ResourceName("Renamed After Archive")));

        Assert.Contains("Archived", exception.Message);
        Assert.Equal("My Resource", resource.Name.Value); // state unchanged
    }

    [Fact]
    public void SetDescription_OnArchivedResource_ThrowsDomainException()
    {
        var resource = CreateArchived();

        var exception = Assert.Throws<DomainException>(
            () => resource.SetDescription("Description after archive"));

        Assert.Contains("Archived", exception.Message);
        Assert.Null(resource.Description); // state unchanged
    }

    [Fact]
    public void SetTags_OnArchivedResource_ThrowsDomainException()
    {
        var resource = CreateArchived();

        var exception = Assert.Throws<DomainException>(
            () => resource.SetTags([new ResourceTag("late")]));

        Assert.Contains("Archived", exception.Message);
        Assert.Single(resource.Tags); // state unchanged (the guard fired before the input check)
    }

    [Fact]
    public void AddTag_OnArchivedResource_ThrowsDomainException()
    {
        var resource = CreateArchived();

        var exception = Assert.Throws<DomainException>(
            () => resource.AddTag(new ResourceTag("late")));

        Assert.Contains("Archived", exception.Message);
        Assert.Single(resource.Tags); // state unchanged
    }

    /// <summary>
    /// The archived guard takes precedence over input validation: renaming an
    /// archived resource with an over-long name must fail as a state conflict
    /// (409), not as bad input (400).
    /// </summary>
    [Fact]
    public void Mutator_OnArchivedResource_StateGuardWinsOverInputValidation()
    {
        var resource = CreateArchived();

        var exception = Assert.Throws<DomainException>(
            () => resource.SetDescription(new string('d', Resource.MaxDescriptionLength + 1)));

        Assert.IsNotType<DomainInputException>(exception);
        Assert.Contains("Archived", exception.Message);
    }

    /// <summary>
    /// Draft and Active resources remain fully mutable: the guard only applies
    /// in the terminal (archived) state.
    /// </summary>
    [Fact]
    public void Mutators_OnActiveResource_AreStillAllowed()
    {
        var resource = Resource.Create(new ResourceName("Old Name"));
        resource.Activate();
        Assert.Equal(ResourceStatus.Active, resource.Status);

        resource.Rename(new ResourceName("New Name"));
        resource.SetDescription("New description");
        resource.SetTags([new ResourceTag("a"), new ResourceTag("b")]);
        resource.AddTag(new ResourceTag("c"));

        Assert.Equal("New Name", resource.Name.Value);
        Assert.Equal("New description", resource.Description);
        Assert.Equal(
            new[] { "a", "b", "c" },
            resource.Tags.Select(static tag => tag.Value).ToArray());
    }

    #endregion

    [Fact]
    public void AddTag_AtMaxTagCount_ThrowsDomainInputException()
    {
        var tags = Enumerable.Range(0, Resource.MaxTagCount)
            .Select(static i => new ResourceTag($"tag-{i}"));
        var resource = Resource.Create(new ResourceName("My Resource"), tags: tags);

        Assert.Throws<DomainInputException>(
            () => resource.AddTag(new ResourceTag("one-more")));
    }

    [Fact]
    public void ClearDomainEvents_EmptiesPendingEvents()
    {
        var resource = Resource.Create(new ResourceName("My Resource"));
        Assert.NotEmpty(resource.DomainEvents);

        ((IClearableDomainEvents)resource).ClearDomainEvents();

        Assert.Empty(resource.DomainEvents);
    }

    [Fact]
    public void IHasDomainEvents_IsExposed()
    {
        var resource = Resource.Create(new ResourceName("My Resource"));

        IHasDomainEvents marker = resource;

        Assert.NotEmpty(marker.DomainEvents);
    }

    [Fact]
    public void Activate_RaisesResourceActivatedEvent_WithFixedClock()
    {
        var fixedTime = new DateTimeOffset(2021, 2, 3, 4, 5, 6, TimeSpan.Zero);
        var resource = Resource.Create(new ResourceName("My Resource"));

        resource.Activate(timeProvider: new FixedTimeProvider(fixedTime));

        Assert.Equal(ResourceStatus.Active, resource.Status);

        var @event = Assert.IsType<ResourceActivatedEvent>(
            resource.DomainEvents.OfType<ResourceActivatedEvent>().Single());

        Assert.Equal(resource.Id, @event.ResourceId);
        Assert.Equal(fixedTime.UtcDateTime, @event.OccurredOn);
    }

    [Fact]
    public void Archive_RaisesResourceArchivedEvent_WithFixedClock()
    {
        var fixedTime = new DateTimeOffset(2021, 2, 3, 4, 5, 6, TimeSpan.Zero);
        var resource = Resource.Create(new ResourceName("My Resource"));
        resource.Activate();

        resource.Archive(timeProvider: new FixedTimeProvider(fixedTime));

        Assert.Equal(ResourceStatus.Archived, resource.Status);

        var @event = Assert.IsType<ResourceArchivedEvent>(
            resource.DomainEvents.OfType<ResourceArchivedEvent>().Single());

        Assert.Equal(resource.Id, @event.ResourceId);
        Assert.Equal(fixedTime.UtcDateTime, @event.OccurredOn);
    }

    [Fact]
    public void Delete_RaisesResourceDeletedEvent_WithoutVersionBump()
    {
        var fixedTime = new DateTimeOffset(2021, 2, 3, 4, 5, 6, TimeSpan.Zero);
        var resource = Resource.Create(new ResourceName("My Resource"));
        resource.Activate();

        var versionBeforeDelete = resource.Version;

        resource.Delete(timeProvider: new FixedTimeProvider(fixedTime));

        // Deletion is a remove, not a state change: the version is left
        // untouched (the remove is guarded by the version loaded in the
        // unit of work).
        Assert.Equal(versionBeforeDelete, resource.Version);

        var @event = Assert.IsType<ResourceDeletedEvent>(
            resource.DomainEvents.OfType<ResourceDeletedEvent>().Single());

        Assert.Equal(resource.Id, @event.ResourceId);
        Assert.Equal(fixedTime.UtcDateTime, @event.OccurredOn);
    }
}
