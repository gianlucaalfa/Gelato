using Gelato.Providers;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>Prepares user-specific streams asynchronously before DTO generation or playback.</summary>
public sealed class StreamSyncService(Lazy<GelatoManager> manager, ILibraryManager library,
    IHostApplicationLifetime lifetime, ILogger<StreamSyncService> log, Lazy<SubtitleProvider> subtitles)
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
                var streamTask = manager.Value.SyncStreams(item, user.Id, token);
                await Task.WhenAll(streamTask, PrewarmSubtitlesAsync(item, token)).ConfigureAwait(false);
                var count = await streamTask.ConfigureAwait(false);
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
    private async Task PrewarmSubtitlesAsync(BaseItem item, CancellationToken ct)
    {
        var options = library.GetLibraryOptions(item);
        if (options.SubtitleDownloadLanguages?.Length is not > 0
            || options.DisabledSubtitleFetchers.Contains("Gelato Subtitles", StringComparer.OrdinalIgnoreCase)
            || StremioUri.FromBaseItem(item) is not { } uri) return;
        try { await subtitles.Value.GetSubtitlesAsync(uri.ExternalId, uri.MediaType, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { log.LogDebug(ex, "Subtitle prewarm failed for item {ItemId}", item.Id); }
    }
}
