using Gelato.Filters;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Gelato.Tests;

public class StreamUserDataFilterTests
{
    [Theory]
    [InlineData(200, true, true)]
    [InlineData(403, true, false)]
    [InlineData(404, true, false)]
    [InlineData(200, false, false)]
    public async Task OnlySuccessfulOwnedMutationsReachThePrimary(int status, bool ownsRow, bool expected)
    {
        var user = new User("test", "auth", "reset");
        var primary = new Movie { Id = Guid.NewGuid() };
        var row = new Movie { Id = Guid.NewGuid(), Tags = [GelatoManager.StreamTag] };
        row.SetPrimaryVersionId(primary.Id);
        row.SetGelatoData("userIds", ownsRow ? new List<Guid> { user.Id } : []);
        var state = new UserItemData { Key = "primary", PlaybackPositionTicks = 123, Likes = true };
        var users = new Mock<IUserManager>();
        users.Setup(x => x.GetUserById(user.Id)).Returns(user);
        var library = new Mock<ILibraryManager>();
        library.Setup(x => x.GetItemById<BaseItem>(row.Id, user)).Returns(row);
        library.Setup(x => x.GetItemById<BaseItem>(primary.Id, user)).Returns(primary);
        var data = new Mock<IUserDataManager>();
        data.Setup(x => x.GetUserData(user, primary)).Returns(state);
        var route = new RouteData();
        route.Values["itemId"] = row.Id.ToString();
        var action = new ActionContext(new DefaultHttpContext(), route,
            new ControllerActionDescriptor { ActionName = "MarkFavoriteItem" });
        var filters = new List<IFilterMetadata>();
        var controller = new object();
        var context = new ActionExecutingContext(action, filters,
            new Dictionary<string, object?> { ["userId"] = user.Id }, controller);
        var filter = new StreamUserDataFilter(library.Object, users.Object, data.Object,
            NullLogger<StreamUserDataFilter>.Instance);
        await filter.OnActionExecutionAsync(context, () => Task.FromResult(
            new ActionExecutedContext(action, filters, controller) { Result = new StatusCodeResult(status) }));
        Assert.Equal(expected, state.IsFavorite);
        Assert.Equal(123, state.PlaybackPositionTicks);
        Assert.True(state.Likes);
        data.Verify(x => x.SaveUserData(user, primary, state, UserDataSaveReason.UpdateUserRating,
            It.IsAny<CancellationToken>()), expected ? Times.Once() : Times.Never());
    }
}
