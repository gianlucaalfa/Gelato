using Gelato.Decorators;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace Gelato.Tests;

public class NativeVersionTests
{
    [Fact]
    public void SeasonChildCountExcludesLinkedStreams()
    {
        var season = Guid.NewGuid();
        var inner = new Mock<IItemCountService>();
        inner.Setup(x => x.GetChildCountBatch(It.IsAny<IReadOnlyList<Guid>>(), null))
            .Returns(new Dictionary<Guid, int> { [season] = 5 });
        var repo = new Mock<IItemRepository>();
        repo.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
            {
                Assert.True(query.IncludeOwnedItems);
                return new List<BaseItem> {
                    new Episode { Id = Guid.NewGuid(), SeasonId = season, Tags = [GelatoManager.StreamTag] },
                    new Episode { Id = Guid.NewGuid(), SeasonId = season, Tags = [GelatoManager.StreamTag] }
                };
            });
        Assert.Equal(3, new ItemCountServiceDecorator(inner.Object, repo.Object).GetChildCountBatch([season], null)[season]);
    }

    [Fact]
    public void PlayedCountsDoNotSubtractLinkedVersionsTwice()
    {
        var season = Guid.NewGuid();
        var filter = new InternalItemsQuery();
        var inner = new Mock<IItemCountService>();
        inner.Setup(x => x.GetPlayedAndTotalCount(filter, season)).Returns((2, 3));
        var repo = new Mock<IItemRepository>();
        repo.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
            {
                Assert.False(query.IncludeOwnedItems);
                return new List<BaseItem>();
            });
        Assert.Equal((2, 3), new ItemCountServiceDecorator(inner.Object, repo.Object).GetPlayedAndTotalCount(filter, season));
    }

    [Fact]
    public void FilteredDtoListsKeepMetadataAttachedToTheCorrectItem()
    {
        var hidden = new Movie { Id = Guid.NewGuid(), Tags = [GelatoManager.StreamTag] };
        var visible = new Movie { Id = Guid.NewGuid() };
        var dto = new BaseItemDto { Id = visible.Id, MediaSourceCount = 2 };
        var options = new DtoOptions(false);
        var inner = new Mock<IDtoService>();
        inner.Setup(x => x.GetBaseItemDtos(It.IsAny<IReadOnlyList<BaseItem>>(), options, null, null, false))
            .Returns(new[] { dto });
        var decorated = new DtoServiceDecorator(inner.Object, new Lazy<GelatoManager>(() => null!),
            new HttpContextAccessor(), Mock.Of<ILibraryManager>(), Mock.Of<IUserDataManager>());
        var result = decorated.GetBaseItemDtos([hidden, visible], options);
        Assert.Single(result);
        Assert.Equal(visible.Id, result[0].Id);
        Assert.Equal(2, result[0].MediaSourceCount);
    }
    [Fact]
    public void SourceCountPreservesManuallyMergedLocalVersions()
    {
        var primary = new Movie { Id = Guid.NewGuid(), Path = "/media/movie.mkv" };
        var local = new Movie { Id = Guid.NewGuid(), Path = "/media/extended.mkv" };
        var row = new Movie { Id = Guid.NewGuid(), Tags = [GelatoManager.StreamTag] };
        var dto = new BaseItemDto { Id = primary.Id, MediaSourceCount = 3 };
        var options = new DtoOptions(false);
        var inner = new Mock<IDtoService>();
        inner.Setup(x => x.GetBaseItemDto(primary, options, null, null)).Returns(dto);
        var library = new Mock<ILibraryManager>();
        library.Setup(x => x.GetLinkedAlternateVersions(primary)).Returns(new Video[] { local, row });
        var decorated = new DtoServiceDecorator(inner.Object, new Lazy<GelatoManager>(() => null!),
            new HttpContextAccessor(), library.Object, Mock.Of<IUserDataManager>());
        Assert.Equal(2, decorated.GetBaseItemDto(primary, options).MediaSourceCount);
    }

}
