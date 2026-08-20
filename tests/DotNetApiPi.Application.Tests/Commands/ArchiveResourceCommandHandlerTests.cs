using DotNetApiPi.Application.Commands;
using DotNetApiPi.Application.Common.Exceptions;
using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.Enums;
using DotNetApiPi.Domain.Exceptions;
using DotNetApiPi.Domain.Repositories;
using Moq;

namespace DotNetApiPi.Application.Tests.Commands;

public sealed class ArchiveResourceCommandHandlerTests
{
    private readonly Mock<IResourceRepository> _repository = new();
    private readonly ArchiveResourceCommandHandler _handler;

    public ArchiveResourceCommandHandlerTests()
    {
        _repository
            .Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _handler = new ArchiveResourceCommandHandler(_repository.Object);
    }

    [Fact]
    public async Task HandleAsync_ArchivesActiveResourceAndReturnsDtoWithArchivedStatus()
    {
        // Arrange
        var resource = ResourceFactory.Active("Some Resource");
        _repository
            .Setup(r => r.GetByIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resource);

        // Act
        var dto = await _handler.HandleAsync(new ArchiveResourceCommand(resource.Id));

        // Assert
        Assert.Equal(resource.Id, dto.Id);
        Assert.Equal(ResourceStatus.Archived.ToString(), dto.Status);
        _repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenResourceIsDraft_ThrowsDomainExceptionWithoutSaving()
    {
        // Arrange
        var resource = ResourceFactory.Draft("Some Resource");
        _repository
            .Setup(r => r.GetByIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resource);

        // Act
        await Assert.ThrowsAsync<DomainException>(
            () => _handler.HandleAsync(new ArchiveResourceCommand(resource.Id)));

        // Assert
        _repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WhenResourceIsAlreadyArchived_ThrowsDomainExceptionWithoutSaving()
    {
        // Arrange
        var resource = ResourceFactory.Archived("Some Resource");
        _repository
            .Setup(r => r.GetByIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resource);

        // Act
        await Assert.ThrowsAsync<DomainException>(
            () => _handler.HandleAsync(new ArchiveResourceCommand(resource.Id)));

        // Assert
        _repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
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
            () => _handler.HandleAsync(new ArchiveResourceCommand(id)));

        // Assert
        Assert.Equal(id, exception.Id);
        _repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WithNullCommand_ThrowsArgumentNullException()
    {
        // Arrange
        ArchiveResourceCommand? command = null;

        // Act
        await Assert.ThrowsAsync<ArgumentNullException>(() => _handler.HandleAsync(command!));
    }

    [Fact]
    public void Constructor_WithNullRepository_ThrowsArgumentNullException()
    {
        // Arrange
        IResourceRepository? repository = null;

        // Act
        Assert.Throws<ArgumentNullException>(() => new ArchiveResourceCommandHandler(repository!));
    }
}
