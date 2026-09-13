using Gelato.Services;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Gelato.Tests;

public class WatchStateTests
{
    [Fact]
    public async Task ResumeUpdatesThePrimaryButClearingAnUnlinkedRowDoesNot()
    {
        var user = new User("test", "auth", "reset");
        var primary = new Movie { Id = Guid.NewGuid() };
        var row = new Movie { Id = Guid.NewGuid(), Tags = [GelatoManager.StreamTag] };
        row.SetPrimaryVersionId(primary.Id);
        var state = new UserItemData { Key = "primary", IsFavorite = true, PlaybackPositionTicks = 5 };
        var users = new Mock<IUserManager>();
        users.Setup(x => x.GetUserById(user.Id)).Returns(user);
        var library = new Mock<ILibraryManager>();
        library.Setup(x => x.GetItemById(primary.Id)).Returns(primary);
        var data = new Mock<IUserDataManager>();
        data.Setup(x => x.GetUserData(user, primary)).Returns(state);
        var sync = new StreamUserDataSync(data.Object, users.Object, library.Object, NullLogger<StreamUserDataSync>.Instance);
        await sync.StartAsync(default);
        data.Raise(x => x.UserDataSaved += null, new UserDataSaveEventArgs {
            UserId = user.Id, Item = row, SaveReason = UserDataSaveReason.PlaybackProgress,
            UserData = new UserItemData { Key = "row", PlaybackPositionTicks = 100 }
        });
        Assert.Equal(100, state.PlaybackPositionTicks);
        Assert.True(state.IsFavorite);
        row.SetPrimaryVersionId(null);
        data.Raise(x => x.UserDataSaved += null, new UserDataSaveEventArgs {
            UserId = user.Id, Item = row, SaveReason = UserDataSaveReason.UpdateUserData,
            UserData = new UserItemData { Key = "row", PlaybackPositionTicks = 0 }
        });
        Assert.Equal(100, state.PlaybackPositionTicks);
        data.Verify(x => x.SaveUserData(user, primary, state, UserDataSaveReason.UpdateUserData, It.IsAny<CancellationToken>()), Times.Once);
        await sync.StopAsync(default);
    }
}
