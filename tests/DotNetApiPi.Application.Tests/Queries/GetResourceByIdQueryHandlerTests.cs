using DotNetApiPi.Application.Common.Exceptions;
using DotNetApiPi.Application.Queries;
using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.Enums;
using DotNetApiPi.Domain.Repositories;
using Moq;

namespace DotNetApiPi.Application.Tests.Queries;

public sealed class GetResourceByIdQueryHandlerTests
{
    private readonly Mock<IResourceRepository> _repository = new();
    private readonly GetResourceByIdQueryHandler _handler;

    public GetResourceByIdQueryHandlerTests()
    {
        _handler = new GetResourceByIdQueryHandler(_repository.Object);
    }

    [Fact]
    public async Task HandleAsync_ReturnsMappedDtoForTheRequestedResource()
    {
        // Arrange
        var resource = ResourceFactory.Active("Some Resource", "A description", "tag1", "tag2");
        _repository
            .Setup(r => r.GetByIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resource);

        // Act
        var dto = await _handler.HandleAsync(new GetResourceByIdQuery(resource.Id));

        // Assert
        Assert.Equal(resource.Id, dto.Id);
        Assert.Equal("Some Resource", dto.Name);
        Assert.Equal("A description", dto.Description);
        Assert.Equal(ResourceStatus.Active.ToString(), dto.Status);
        Assert.Equal(new HashSet<string> { "tag1", "tag2" }, dto.Tags.ToHashSet());
        _repository.Verify(
            r => r.GetByIdAsync(resource.Id, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenResourceDoesNotExist_ThrowsResourceNotFoundException()
    {
        // Arrange
        _repository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Resource?)null);
        var id = Guid.NewGuid();

        // Act
        var exception = await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => _handler.HandleAsync(new GetResourceByIdQuery(id)));

        // Assert
        Assert.Equal(id, exception.Id);
    }

    [Fact]
    public async Task HandleAsync_WithNullQuery_ThrowsArgumentNullException()
    {
        // Arrange
        GetResourceByIdQuery? query = null;

        // Act
        await Assert.ThrowsAsync<ArgumentNullException>(() => _handler.HandleAsync(query!));
    }

    [Fact]
    public void Constructor_WithNullRepository_ThrowsArgumentNullException()
    {
        // Arrange
        IResourceRepository? repository = null;

        // Act
        Assert.Throws<ArgumentNullException>(() => new GetResourceByIdQueryHandler(repository!));
    }
}
