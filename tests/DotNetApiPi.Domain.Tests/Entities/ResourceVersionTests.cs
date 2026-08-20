using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.Enums;
using DotNetApiPi.Domain.ValueObjects;

namespace DotNetApiPi.Domain.Tests.Entities;

/// <summary>
/// Pins the optimistic-concurrency version semantics of the
/// <see cref="Resource"/> aggregate: every real state change bumps
/// <see cref="Resource.Version"/> by exactly one, no-ops do not, and
/// reconstitution preserves the persisted version.
/// </summary>
public sealed class ResourceVersionTests
{
    [Fact]
    public void Create_StartsAtVersionZero()
    {
        var resource = Resource.Create(new ResourceName("Fresh"));

        Assert.Equal(0, resource.Version);
    }

    [Fact]
    public void Rename_BumpsVersion_Once()
    {
        var resource = Resource.Create(new ResourceName("Before"));

        resource.Rename(new ResourceName("After"));

        Assert.Equal(1, resource.Version);
    }

    [Fact]
    public void Rename_ToSameName_DoesNotBumpVersion()
    {
        var resource = Resource.Create(new ResourceName("Same Name"));

        // No-op rename: the aggregate state is unchanged, so the version —
        // and therefore other clients' ETags — must stay stable.
        resource.Rename(new ResourceName("Same Name"));

        Assert.Equal(0, resource.Version);
    }

    [Fact]
    public void SetDescription_BumpsVersion_Once()
    {
        var resource = Resource.Create(new ResourceName("Named"));

        resource.SetDescription("A description");

        Assert.Equal(1, resource.Version);
    }

    [Fact]
    public void SetDescription_ToSameValue_DoesNotBumpVersion()
    {
        var resource = Resource.Create(
            new ResourceName("Named"),
            description: "Unchanged");

        resource.SetDescription("Unchanged");

        Assert.Equal(0, resource.Version);
    }

    [Fact]
    public void Activate_BumpsVersion_Once()
    {
        var resource = Resource.Create(new ResourceName("Draft"));

        resource.Activate();

        Assert.Equal(1, resource.Version);
    }

    [Fact]
    public void Archive_BumpsVersion_Once()
    {
        var resource = Resource.Create(new ResourceName("Draft"));
        resource.Activate();

        resource.Archive();

        Assert.Equal(2, resource.Version);
    }

    [Fact]
    public void AddTag_BumpsVersion_Once()
    {
        var resource = Resource.Create(new ResourceName("Untagged"));

        resource.AddTag(new ResourceTag("first"));
        resource.AddTag(new ResourceTag("second"));

        Assert.Equal(2, resource.Version);
    }

    [Fact]
    public void AddTag_WithExistingTag_DoesNotBumpVersion()
    {
        var resource = Resource.Create(
            new ResourceName("Tagged"),
            tags: [new ResourceTag("existing")]);

        resource.AddTag(new ResourceTag("existing"));

        Assert.Equal(0, resource.Version);
    }

    [Fact]
    public void SetTags_WithDifferentSet_BumpsVersion_Once()
    {
        var resource = Resource.Create(
            new ResourceName("Tagged"),
            tags: [new ResourceTag("a"), new ResourceTag("b")]);

        resource.SetTags([new ResourceTag("a"), new ResourceTag("c")]);

        Assert.Equal(1, resource.Version);
    }

    [Fact]
    public void SetTags_WithIdenticalSet_DoesNotBumpVersion()
    {
        var resource = Resource.Create(
            new ResourceName("Tagged"),
            tags: [new ResourceTag("a"), new ResourceTag("b")]);

        // Same set, same order: the aggregate state is unchanged.
        resource.SetTags([new ResourceTag("a"), new ResourceTag("b")]);

        Assert.Equal(0, resource.Version);
    }

    [Fact]
    public void Mutators_BumpVersion_OnceEach()
    {
        var resource = Resource.Create(new ResourceName("v0"));

        resource.Rename(new ResourceName("v1"));
        resource.SetDescription("v2");
        resource.AddTag(new ResourceTag("v3"));
        resource.Activate();
        resource.Archive();

        Assert.Equal(5, resource.Version);
    }

    [Fact]
    public void Reconstitute_PreservesThePersistedVersion()
    {
        var resource = Resource.Reconstitute(
            Guid.NewGuid(),
            new ResourceName("Loaded"),
            "Loaded description",
            ResourceStatus.Active,
            [new ResourceTag("loaded")],
            version: 7);

        Assert.Equal(7, resource.Version);
    }

    [Fact]
    public void ReconstitutedResource_Mutators_BumpFromThePersistedVersion()
    {
        var resource = Resource.Reconstitute(
            Guid.NewGuid(),
            new ResourceName("Loaded"),
            null,
            ResourceStatus.Draft,
            null,
            version: 3);

        resource.Rename(new ResourceName("Loaded Renamed"));

        Assert.Equal(4, resource.Version);
    }
}
