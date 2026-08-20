using DotNetApiPi.Application.Commands;
using DotNetApiPi.Application.Common.Exceptions;
using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.Exceptions;
using DotNetApiPi.Domain.Repositories;
using Moq;

namespace DotNetApiPi.Application.Tests.Commands;

public sealed class UpdateResourceCommandHandlerTests
{
    private readonly Mock<IResourceRepository> _repository = new();
    private readonly UpdateResourceCommandHandler _handler;

    public UpdateResourceCommandHandlerTests()
    {
        _repository
            .Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _handler = new UpdateResourceCommandHandler(_repository.Object);
    }

    private void SetupFoundResource(Resource resource)
    {
        _repository
            .Setup(r => r.GetByIdAsync(resource.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resource);
    }

    [Fact]
    public async Task HandleAsync_AppliesNewNameDescriptionAndTagsAndReturnsUpdatedDto()
    {
        // Arrange
        var resource = ResourceFactory.Draft("Old Name", "Old description", "oldtag");
        SetupFoundResource(resource);
        var command = new UpdateResourceCommand(
            resource.Id, "New Name", "New description", new[] { "newtag" });

        // Act
        var dto = await _handler.HandleAsync(command);

        // Assert
        Assert.Equal("New Name", dto.Name);
        Assert.Equal("New description", dto.Description);
        Assert.Equal(new HashSet<string> { "newtag" }, dto.Tags.ToHashSet());
        _repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WithNullTags_KeepsExistingTags()
    {
        // Arrange
        var resource = ResourceFactory.Draft("Some Name", null, "keep");
        SetupFoundResource(resource);
        var command = new UpdateResourceCommand(resource.Id, "Some Name", null, null);

        // Act
        var dto = await _handler.HandleAsync(command);

        // Assert
        Assert.Equal(new HashSet<string> { "keep" }, dto.Tags.ToHashSet());
    }

    [Fact]
    public async Task HandleAsync_WhenResourceDoesNotExist_ThrowsResourceNotFoundExceptionWithoutSaving()
    {
        // Arrange
        _repository
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Resource?)null);
        var id = Guid.NewGuid();

        // Act
        var exception = await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => _handler.HandleAsync(new UpdateResourceCommand(id, "New Name", null, null)));

        // Assert
        Assert.Equal(id, exception.Id);
        _repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WithBlankName_ThrowsDomainInputExceptionWithoutSaving()
    {
        // Arrange
        var resource = ResourceFactory.Draft("Existing Name");
        SetupFoundResource(resource);

        // Act
        await Assert.ThrowsAsync<DomainInputException>(
            () => _handler.HandleAsync(new UpdateResourceCommand(resource.Id, "  ", null, null)));

        // Assert
        _repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WithNullCommand_ThrowsArgumentNullException()
    {
        // Arrange
        UpdateResourceCommand? command = null;

        // Act
        await Assert.ThrowsAsync<ArgumentNullException>(() => _handler.HandleAsync(command!));
    }

    [Fact]
    public void Constructor_WithNullRepository_ThrowsArgumentNullException()
    {
        // Arrange
        IResourceRepository? repository = null;

        // Act
        Assert.Throws<ArgumentNullException>(() => new UpdateResourceCommandHandler(repository!));
    }
}
