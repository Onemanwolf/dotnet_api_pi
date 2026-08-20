using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.Enums;
using DotNetApiPi.Domain.ValueObjects;
using DotNetApiPi.Infrastructure.Persistence.Mongo;

namespace DotNetApiPi.Infrastructure.Tests;

/// <summary>
/// Tests for the <see cref="ResourceDocumentMapper"/> document round-trip.
/// </summary>
public sealed class ResourceDocumentMapperTests
{
    [Fact]
    public void ToDocument_MapsAllFields()
    {
        var resource = Resource.Create(
            new ResourceName("Documented Resource"),
            "A description",
            [new ResourceTag("Cloud"), new ResourceTag("Fast")]);
        resource.Activate();

        var document = ResourceDocumentMapper.ToDocument(resource);

        Assert.Equal(resource.Id, document.Id);
        Assert.Equal("Documented Resource", document.Name);
        Assert.Equal("A description", document.Description);
        Assert.Equal(ResourceStatus.Active.ToString(), document.Status);
        Assert.Equal(
            new[] { "cloud", "fast" },
            document.Tags.OrderBy(static tag => tag).ToArray());
    }

    [Fact]
    public void ToDocument_NullDescription_RemainsNull()
    {
        var resource = Resource.Create(new ResourceName("No Description"));

        var document = ResourceDocumentMapper.ToDocument(resource);

        Assert.Null(document.Description);
    }

    [Fact]
    public void ToAggregate_RoundTripsAllFields()
    {
        var resource = Resource.Create(
            new ResourceName("Round Trip"),
            "Back and forth",
            [new ResourceTag("alpha"), new ResourceTag("Beta")]);
        resource.Activate();
        resource.Archive();

        var aggregate = ResourceDocumentMapper.ToAggregate(
            ResourceDocumentMapper.ToDocument(resource));

        Assert.Equal(resource.Id, aggregate.Id);
        Assert.Equal(resource.Name.Value, aggregate.Name.Value);
        Assert.Equal(resource.Description, aggregate.Description);
        Assert.Equal(resource.Status, aggregate.Status);
        Assert.Equal(
            resource.Tags.Select(static tag => tag.Value).OrderBy(v => v),
            aggregate.Tags.Select(static tag => tag.Value).OrderBy(v => v));
    }

    [Fact]
    public void ToAggregate_EmptyTags_YieldsEmptyCollection()
    {
        var resource = Resource.Create(new ResourceName("Tagless"));

        var aggregate = ResourceDocumentMapper.ToAggregate(
            ResourceDocumentMapper.ToDocument(resource));

        Assert.Empty(aggregate.Tags);
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Active")]
    [InlineData("Archived")]
    public void ToAggregate_ParsesStoredStatusString(string status)
    {
        var resource = Resource.Create(new ResourceName("Statusful"));
        var document = ResourceDocumentMapper.ToDocument(resource);
        document.Status = status;

        var aggregate = ResourceDocumentMapper.ToAggregate(document);

        Assert.Equal(Enum.Parse<ResourceStatus>(status), aggregate.Status);
    }
}
