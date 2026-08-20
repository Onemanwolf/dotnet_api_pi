using DotNetApiPi.Application.Queries;
using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.Enums;
using DotNetApiPi.Domain.Repositories;
using Moq;

namespace DotNetApiPi.Application.Tests.Queries;

public sealed class GetAllResourcesQueryHandlerTests
{
    private readonly Mock<IResourceRepository> _repository = new();
    private readonly GetAllResourcesQueryHandler _handler;

    public GetAllResourcesQueryHandlerTests()
    {
        _handler = new GetAllResourcesQueryHandler(_repository.Object);
    }

    [Fact]
    public async Task HandleAsync_ReturnsAMappedDtoForEveryResource()
    {
        // Arrange
        var draft = ResourceFactory.Draft("Draft Resource");
        var active = ResourceFactory.Active("Active Resource");
        var archived = ResourceFactory.Archived("Archived Resource");
        _repository
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { draft, active, archived });

        // Act
        var dtos = await _handler.HandleAsync(new GetAllResourcesQuery());

        // Assert
        Assert.Equal(3, dtos.Count);
        Assert.Contains(dtos, dto => dto.Id == draft.Id && dto.Status == ResourceStatus.Draft.ToString());
        Assert.Contains(dtos, dto => dto.Id == active.Id && dto.Status == ResourceStatus.Active.ToString());
        Assert.Contains(dtos, dto => dto.Id == archived.Id && dto.Status == ResourceStatus.Archived.ToString());
        _repository.Verify(r => r.GetAllAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenThereAreNoResources_ReturnsAnEmptyList()
    {
        // Arrange
        _repository
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Resource>());

        // Act
        var dtos = await _handler.HandleAsync(new GetAllResourcesQuery());

        // Assert
        Assert.Empty(dtos);
    }

    [Fact]
    public void Constructor_WithNullRepository_ThrowsArgumentNullException()
    {
        // Arrange
        IResourceRepository? repository = null;

        // Act
        Assert.Throws<ArgumentNullException>(() => new GetAllResourcesQueryHandler(repository!));
    }
}
