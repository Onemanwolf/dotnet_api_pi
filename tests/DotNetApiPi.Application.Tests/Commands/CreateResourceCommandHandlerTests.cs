using DotNetApiPi.Application.Commands;
using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.Enums;
using DotNetApiPi.Domain.Exceptions;
using DotNetApiPi.Domain.Repositories;
using Moq;

namespace DotNetApiPi.Application.Tests.Commands;

public sealed class CreateResourceCommandHandlerTests
{
    private readonly Mock<IResourceRepository> _repository = new();
    private readonly CreateResourceCommandHandler _handler;

    public CreateResourceCommandHandlerTests()
    {
        _repository
            .Setup(r => r.AddAsync(It.IsAny<Resource>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Resource resource, CancellationToken _) => resource);
        _repository
            .Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _handler = new CreateResourceCommandHandler(_repository.Object);
    }

    [Fact]
    public async Task HandleAsync_CreatesResourcePersistsItAndReturnsMappedDto()
    {
        // Arrange
        var command = new CreateResourceCommand(" My Resource ", "A description",
            new[] { "Alpha", "beta", "alpha" });

        // Act
        var dto = await _handler.HandleAsync(command);

        // Assert
        Assert.Equal("My Resource", dto.Name);
        Assert.Equal("A description", dto.Description);
        Assert.Equal(ResourceStatus.Draft.ToString(), dto.Status);
        Assert.Equal(new HashSet<string> { "alpha", "beta" }, dto.Tags.ToHashSet());

        _repository.Verify(r => r.AddAsync(
            It.Is<Resource>(resource => resource.Id == dto.Id
                && resource.Name.Value == "My Resource"
                && resource.Status == ResourceStatus.Draft),
            It.IsAny<CancellationToken>()),
            Times.Once);
        _repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WithoutTags_ReturnsDtoWithEmptyTagList()
    {
        // Arrange
        var command = new CreateResourceCommand("My Resource", null, null);

        // Act
        var dto = await _handler.HandleAsync(command);

        // Assert
        Assert.Empty(dto.Tags);
    }

    [Fact]
    public async Task HandleAsync_WithBlankName_ThrowsDomainInputExceptionWithoutPersisting()
    {
        // Arrange
        var command = new CreateResourceCommand("   ", "A description", null);

        // Act
        await Assert.ThrowsAsync<DomainInputException>(() => _handler.HandleAsync(command));

        // Assert
        _repository.Verify(r => r.AddAsync(It.IsAny<Resource>(), It.IsAny<CancellationToken>()), Times.Never);
        _repository.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WithNullCommand_ThrowsArgumentNullException()
    {
        // Arrange
        CreateResourceCommand? command = null;

        // Act
        await Assert.ThrowsAsync<ArgumentNullException>(() => _handler.HandleAsync(command!));
    }

    [Fact]
    public void Constructor_WithNullRepository_ThrowsArgumentNullException()
    {
        // Arrange
        IResourceRepository? repository = null;

        // Act
        Assert.Throws<ArgumentNullException>(() => new CreateResourceCommandHandler(repository!));
    }
}
