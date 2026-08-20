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

        var exception = Assert.Throws<DomainException>(resource.Activate);

        Assert.Contains("Draft", exception.Message);
    }

    [Fact]
    public void Activate_FromArchived_ThrowsDomainException()
    {
        var resource = Resource.Create(new ResourceName("My Resource"));
        resource.Activate();
        resource.Archive();

        var exception = Assert.Throws<DomainException>(resource.Activate);

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

        var exception = Assert.Throws<DomainException>(resource.Archive);

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
}
