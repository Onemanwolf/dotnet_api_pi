using DotNetApiPi.Application.Commands;
using DotNetApiPi.Application.Common;
using DotNetApiPi.Application.Common.Exceptions;
using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.Repositories;
using Moq;

namespace DotNetApiPi.Application.Tests.Commands;

public sealed class DeleteResourceCommandHandlerTests
{
    private readonly Mock<IResourceRepository> _repository = new();
    private readonly DeleteResourceCommandHandler _handler;

    public DeleteResourceCommandHandlerTests()
    {
        _repository
            .Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _handler = new DeleteResourceCommandHandler(_repository.Object);
    }

    [Fact]
    public async Task HandleAsync_RemovesTheResourcePersistsTheChangeAndReturnsUnit()
    {
        // Arrange
        var resource = ResourceFactory.Draft("Some Resource");
        _repository
            .Setup(r => r.GetByIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resource);

        // Act
        var result = await _handler.HandleAsync(new DeleteResourceCommand(resource.Id));

        // Assert
        Assert.Equal(Unit.Value, result);
        _repository.Verify(
            r => r.RemoveAsync(It.Is<Resource>(removed => removed.Id == resource.Id),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenResourceDoesNotExist_ThrowsResourceNotFoundExceptionWithoutRemoving()
    {
        // Arrange
        _repository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Resource?)null);
        var id = Guid.NewGuid();

        // Act
        var exception = await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => _handler.HandleAsync(new DeleteResourceCommand(id)));

        // Assert
        Assert.Equal(id, exception.Id);
        _repository.Verify(r => r.RemoveAsync(It.IsAny<Resource>(), It.IsAny<CancellationToken>()), Times.Never);
        _repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WithNullCommand_ThrowsArgumentNullException()
    {
        // Arrange
        DeleteResourceCommand? command = null;

        // Act
        await Assert.ThrowsAsync<ArgumentNullException>(() => _handler.HandleAsync(command!));
    }

    [Fact]
    public void Constructor_WithNullRepository_ThrowsArgumentNullException()
    {
        // Arrange
        IResourceRepository? repository = null;

        // Act
        Assert.Throws<ArgumentNullException>(() => new DeleteResourceCommandHandler(repository!));
    }
}
