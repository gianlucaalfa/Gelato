using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.ScheduledTasks;

public sealed class PurgeGelatoStreamsTask(
    ILibraryManager libraryManager,
    IItemPersistenceService persistence,
    ILogger<PurgeGelatoStreamsTask> log,
    GelatoManager manager
) : IScheduledTask
{
    public string Name => "Purge streams";
    public string Key => "PurgeGelatoStreamsTask";
    public string Description => "Removes all stremio streams";
    public string Category => "Gelato Maintenance";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromDays(7).Ticks,
            },
        ];
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        log.LogInformation("purging streams");

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            Recursive = true,
            HasAnyProviderId = new Dictionary<string, string>
            {
                { "Stremio", string.Empty },
                { "stremio", string.Empty },
            },
            IsDeadPerson = true,
            // Stream rows are alternate versions, which Jellyfin leaves out of queries by default.
            IncludeOwnedItems = true,
        };

        var streams = libraryManager
            .GetItemList(query)
            .OfType<Video>()
            .Where(v => v.IsStream())
            .ToArray();

        var done = 0;
        foreach (var group in streams.GroupBy(v => v.PrimaryVersionId ?? v.Id))
        {
            await manager.RunStreamMutationAsync(group.Key, token =>
            {
                // Reload inside the same lock used by synchronization and manual deletion.
                var current = group.Select(row => libraryManager.GetItemById(row.Id))
                    .OfType<Video>().Where(row => row.HasStreamTag()).ToArray();
                if (libraryManager.GetItemById(group.Key) is Video primary && !primary.HasStreamTag())
                {
                    var ids = current.Select(row => row.Id).ToHashSet();
                    primary.LinkedAlternateVersions = primary.LinkedAlternateVersions
                        .Where(link => link.ItemId is not { } id || !ids.Contains(id)).ToArray();
                    persistence.SaveItems([primary], token);
                    libraryManager.RegisterItem(primary);
                }
                foreach (var row in current) row.SetPrimaryVersionId(null);
                manager.ForgetWatchState(current, token);
                foreach (var row in current)
                {
                    token.ThrowIfCancellationRequested();
                    try { libraryManager.DeleteItem(row, new DeleteOptions { DeleteFileLocation = false }, true); }
                    catch (Exception ex) { log.LogWarning(ex, "Failed to delete stream {ItemId}", row.Id); }
                }
                return Task.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
            done += group.Count();
            progress?.Report(100.0 * done / Math.Max(1, streams.Length));
        }

        progress?.Report(100.0);
        manager.ClearCache();

        log.LogInformation("stream purge completed");
    }
}
