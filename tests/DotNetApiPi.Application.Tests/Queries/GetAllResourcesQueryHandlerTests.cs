using DotNetApiPi.Application.Dtos;
using DotNetApiPi.Application.Queries;
using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.Enums;
using DotNetApiPi.Domain.Exceptions;
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
            .Setup(r => r.GetPageAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<Resource>)[draft, active, archived], 3));

        // Act
        var paged = await _handler.HandleAsync(new GetAllResourcesQuery());

        // Assert
        Assert.Equal(3, paged.Items.Count);
        Assert.Contains(paged.Items, dto => dto.Id == draft.Id && dto.Status == ResourceStatus.Draft.ToString());
        Assert.Contains(paged.Items, dto => dto.Id == active.Id && dto.Status == ResourceStatus.Active.ToString());
        Assert.Contains(paged.Items, dto => dto.Id == archived.Id && dto.Status == ResourceStatus.Archived.ToString());
        Assert.Equal(3, paged.TotalCount);
        Assert.Equal(1, paged.Page);
        Assert.Equal(20, paged.PageSize);
        Assert.Equal(1, paged.TotalPages);
        _repository.Verify(
            r => r.GetPageAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_ForwardsPagingParameters_AndComputesTotalPages()
    {
        // Arrange
        var pageItems = Enumerable
            .Range(0, 5)
            .Select(static index => ResourceFactory.Draft($"Paged {index}"))
            .ToList();
        _repository
            .Setup(r => r.GetPageAsync(2, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync((pageItems, 23));

        // Act
        var paged = await _handler
            .HandleAsync(new GetAllResourcesQuery(Page: 2, PageSize: 5));

        // Assert
        Assert.Equal(5, paged.Items.Count);
        Assert.Equal(23, paged.TotalCount);
        Assert.Equal(2, paged.Page);
        Assert.Equal(5, paged.PageSize);
        // 23 items at 5 per page = 5 pages (last page partial).
        Assert.Equal(5, paged.TotalPages);
        _repository.Verify(
            r => r.GetPageAsync(2, 5, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenThereAreNoResources_ReturnsAnEmptyPage()
    {
        // Arrange
        _repository
            .Setup(r => r.GetPageAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<Resource>)Array.Empty<Resource>(), 0));

        // Act
        var paged = await _handler.HandleAsync(new GetAllResourcesQuery());

        // Assert
        Assert.Empty(paged.Items);
        Assert.Equal(0, paged.TotalCount);
        Assert.Equal(0, paged.TotalPages);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-3, 20)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 101)]
    public async Task HandleAsync_WithOutOfRangePaging_ThrowsDomainInputException(
        int page,
        int pageSize)
    {
        // Arrange
        var query = new GetAllResourcesQuery(page, pageSize);

        // Act / Assert: the handler defends against out-of-range paging even
        // when it is not called through the HTTP boundary.
        await Assert.ThrowsAsync<DomainInputException>(
            () => _handler.HandleAsync(query));

        _repository.VerifyNoOtherCalls();
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
