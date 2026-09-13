using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>Prepares user-specific streams asynchronously before DTO generation or playback.</summary>
public sealed class StreamSyncService(Lazy<GelatoManager> manager, ILibraryManager library,
    IHostApplicationLifetime lifetime, ILogger<StreamSyncService> log)
{
    private readonly KeyLock _requests = new();

    public async Task PrepareAsync(BaseItem item, User user, CancellationToken ct)
    {
        var cfg = GelatoPlugin.Instance!.GetConfig(user.Id);
        if (cfg.Stremio is null || item is not (MediaBrowser.Controller.Entities.Movies.Movie or MediaBrowser.Controller.Entities.TV.Episode))
            return;
        if (item is Video { PrimaryVersionId: { } id } && item.HasStreamTag())
            item = library.GetItemById<Video>(id, user) ?? item;
        if (item.HasStreamTag() || (!item.IsGelato() && !cfg.EnableMixed)) return;
        var key = $"{user.Id}:{item.Id}";
        if (manager.Value.HasStreamSync(key)) return;
        // A disconnected waiter must not cancel work another client is already awaiting.
        var work = _requests.RunSingleFlightAsync(key, async token =>
        {
            if (manager.Value.HasStreamSync(key)) return;
            try
            {
                var count = await manager.Value.SyncStreams(item, user.Id, token).ConfigureAwait(false);
                manager.Value.SetStreamSync(key, count == 0 ? TimeSpan.FromSeconds(30) : null);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Unable to refresh streams for item {ItemId}", item.Id);
                manager.Value.SetStreamSync(key, TimeSpan.FromSeconds(5));
            }
        }, lifetime.ApplicationStopping);
        await work.WaitAsync(ct).ConfigureAwait(false);
    }
}
