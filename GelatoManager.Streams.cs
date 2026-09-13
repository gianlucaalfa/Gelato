using System.Diagnostics;
using System.Globalization;
using Gelato.Config;
using Gelato.Services;
using Gelato.Decorators;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Gelato;

public sealed partial class GelatoManager
{
    private readonly KeyLock _streamWrites = new();

    internal Task RunStreamMutationAsync(Guid itemId, Func<CancellationToken, Task> action, CancellationToken ct) =>
        _streamWrites.RunQueuedAsync(itemId, action, ct);

    public Task<int> SyncStreams(BaseItem item, Guid userId, CancellationToken ct) =>
        _streamWrites.RunQueuedAsync(item.Id,
            token => libraryManager.GetItemById(item.Id) is { } current
                ? SyncStreamsCore(current, userId, token)
                : Task.FromResult(0), ct);

    private async Task<int> SyncStreamsCore(BaseItem item, Guid userId, CancellationToken ct)
    {
        _log.LogDebug($"SyncStreams for {item.Id}");
        var stopwatch = Stopwatch.StartNew();
        if (item is not Video video)
        {
            _log.LogWarning(
                "SyncStreams: item is not a Video type, itemType={ItemType}",
                item.GetType().Name
            );
            return 0;
        }

        if (video.IsStream() || video.PrimaryVersionId is not null)
        {
            _log.LogWarning("SyncStreams: item is a stream, skipping");
            return 0;
        }

        var isEpisode = video is Episode;
        var parent = video.GetParent() as Folder ?? (isEpisode ? null : TryGetMovieFolder(userId));
        if (parent is null)
        {
            _log.LogWarning("SyncStreams: no parent, skipping");
            return 0;
        }

        var uri = StremioUri.FromBaseItem(video);
        if (uri is null)
        {
            _log.LogError($"Unable to build Stremio URI for {video.Name}");
            return 0;
        }

        var streamProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (providerId, value) in video.ProviderIds)
        {
            streamProviderIds[providerId] = value;
        }
        streamProviderIds["Stremio"] = uri.ExternalId;

        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        var stremio = cfg.Stremio;
        if (stremio is null) return 0;
        var streams = await stremio.GetStreamsAsync(uri, ct).ConfigureAwait(false);
        var httpPort = GetHttpPort();

        // Filter valid streams
        var acceptable = streams
            .Select(s =>
            {
                if (!s.IsValid())
                {
                    _log.LogWarning("Invalid stream, skipping {StreamName}", s.Name);
                    return null;
                }

                if (!cfg.P2PEnabled && s.IsTorrent())
                {
                    _log.LogDebug($"P2P stream, skipping {s.Name}");
                    return null;
                }

                return s;
            })
            .Where(s => s is not null)
            .ToList();

        // Get existing streams. A movie's rows only by Stremio id: movies of a collection share
        // the TmdbCollection id, and the other movies' rows would be treated as stale below.
        // Episodes keep matching on all ids, which also finds rows synced under an older
        // Stremio id.
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [isEpisode ? BaseItemKind.Episode : BaseItemKind.Movie],
            HasAnyProviderId = isEpisode
                ? streamProviderIds
                : new Dictionary<string, string> { { "Stremio", uri.ExternalId } },
            Recursive = true,
            IsDeadPerson = true,
            ParentId = parent.Id,
            // Rows are alternate versions, which Jellyfin leaves out of queries by default.
            IncludeOwnedItems = true,
            //  IsVirtualItem = true,
        };

        var existingStreamItems = repo.GetItemList(query)
            .OfType<Video>()
            .Where(v => v.IsStream() && (v.PrimaryVersionId is null || v.PrimaryVersionId == video.Id))
            .ToList();

        // Match stream rows by persisted Gelato guid, not by volatile playback URL/path.
        var existingByGuid = new Dictionary<Guid, Video>();
        var duplicates = new List<Video>();
        foreach (var existingItem in existingStreamItems)
        {
            var existingGuid = existingItem.GelatoData<Guid?>("guid");
            if (existingGuid is null || existingGuid == Guid.Empty)
            {
                // Strict guid matching: ignore rows without a persisted guid.
                continue;
            }

            if (!existingByGuid.TryAdd(existingGuid.Value, existingItem))
            {
                // Guard against bad historical data; don't fail sync on collisions.
                duplicates.Add(existingItem);
                _log.LogWarning(
                    "Duplicate stream guid found during sync: {Guid}. Keeping first item id={FirstId}, deleting item id={SecondId}",
                    existingGuid.Value,
                    existingByGuid[existingGuid.Value].Id,
                    existingItem.Id
                );
            }
        }

        var upsertedStreams = new List<Video>();
        var now = DateTime.UtcNow;

        for (var i = 0; i < acceptable.Count; i++)
        {
            var s = acceptable[i];
            var index = i + 1;
            var path = s.IsFile()
                ? s.Url
                : $"http://127.0.0.1:{httpPort}/gelato/stream?ih={s.InfoHash}"
                    + (s.FileIdx is not null ? $"&idx={s.FileIdx}" : "")
                    + (
                        s.Sources is { Count: > 0 }
                            ? $"&trackers={Uri.EscapeDataString(string.Join(',', s.Sources))}"
                            : ""
                    );

            if (!s.IsFile())
            {
                var trackers = s.Sources is { Count: > 0 } ? string.Join(',', s.Sources) : null;
                path += "&token=" + Uri.EscapeDataString(torrentAccess.Create(s.InfoHash!, s.FileIdx, trackers));
            }

            var streamGuid = s.GetGuid();
            var isNewStreamItem = !existingByGuid.TryGetValue(streamGuid, out var streamItem);

            if (isNewStreamItem)
            {
                streamItem =
                    isEpisode && video is Episode e
                        ? new Episode
                        {
                            //Id = libraryManager.GetNewItemId(path, typeof(Episode)),
                            SeriesId = e.SeriesId,
                            SeriesName = e.SeriesName,
                            SeasonId = e.SeasonId,
                            SeasonName = e.SeasonName,
                            IndexNumber = e.IndexNumber,
                            ParentIndexNumber = e.ParentIndexNumber,
                            PremiereDate = e.PremiereDate,
                        }
                        : new Movie
                        {
                            //Id = libraryManager.GetNewItemId(path, typeof(Movie))
                        };
                streamItem.Path = path;
                streamItem.Id = libraryManager.GetNewItemId($"gelato-stream://{video.Id:N}/{streamGuid:N}", streamItem.GetType());
            }

            streamItem.Tags = [StreamTag];

            var locked = streamItem.LockedFields?.ToList() ?? [];
            if (!locked.Contains(MetadataField.Tags))
                locked.Add(MetadataField.Tags);
            streamItem.LockedFields = locked.ToArray();

            streamItem.ProviderIds = streamProviderIds;
            streamItem.RunTimeTicks = video.RunTimeTicks;
            streamItem.LinkedAlternateVersions = [];
            streamItem.SetPrimaryVersionId(video.Id);
            CopyVersionMetadata(video, streamItem);
            streamItem.Path = path;
            streamItem.IsVirtualItem = false;
            streamItem.SetParent(parent);

            var users = streamItem.GelatoData<List<Guid>>("userIds") ?? [];
            if (!users.Contains(userId))
            {
                users.Add(userId);
                streamItem.SetGelatoData("userIds", users);
            }

            streamItem.SetGelatoData("name", s.Name);
            streamItem.SetGelatoData("description", s.Description);
            if (!string.IsNullOrEmpty(s.BehaviorHints?.BingeGroup))
            {
                streamItem.SetGelatoData("bingeGroup", s.BehaviorHints.BingeGroup);
            }
            if (!string.IsNullOrEmpty(s.BehaviorHints?.Filename))
            {
                streamItem.SetGelatoData("filename", s.BehaviorHints.Filename);
            }
            streamItem.SetGelatoData("index", index);
            streamItem.SetGelatoData("guid", streamGuid);
            // Keep map current so stale detection below uses the final upserted set.
            existingByGuid[streamGuid] = streamItem;

            // Prevent a library scan from treating remote rows as unrefreshed new items.
            streamItem.DateLastRefreshed = now;
            streamItem.DateLastSaved = now;

            upsertedStreams.Add(streamItem);
        }

        //upsertedStreams = SaveItems(upsertedStreams, (Folder)primary.GetParent()).Cast<Video>().ToList();
        persistence.SaveItems(upsertedStreams, ct);

        var newIds = new HashSet<Guid>(upsertedStreams.Select(x => x.Id));
        var stale = existingByGuid
            .Values.Where(m =>
                !newIds.Contains(m.Id)
                && (m.GelatoData<List<Guid>>("userIds")?.Contains(userId) ?? false)
            )
            .ToList();

        foreach (var _item in stale)
        {
            var users = _item.GelatoData<List<Guid>>("userIds") ?? [];
            users.Remove(userId);
            _item.SetGelatoData("userIds", users);
        }

        var toDelete = stale
            .Where(item => item.GelatoData<List<Guid>>("userIds") is { Count: 0 })
            .Concat(duplicates)
            .ToList();
        var toSave = stale.Except(toDelete).ToList();

        // Preserve other users' legacy rows while assigning their native-version owner.
        var kept = existingByGuid.Values.Except(toDelete).ToList();
        foreach (var row in kept.Except(upsertedStreams))
        {
            if (row.PrimaryVersionId == video.Id && row.DateLastRefreshed != DateTime.MinValue)
                continue;
            row.SetPrimaryVersionId(video.Id);
            if (row.DateLastRefreshed == DateTime.MinValue) row.DateLastRefreshed = now;
            row.DateLastSaved = now;
            if (!toSave.Contains(row)) toSave.Add(row);
        }

        persistence.SaveItems(toSave, ct);

        // Rows are loaded here straight from the database, and saved around LibraryManager: without
        // this, version pages and the source list keep getting the rows as they were cached before.
        foreach (var row in upsertedStreams.Concat(toSave))
        {
            libraryManager.RegisterItem(row);
        }

        // Every row some user still has is a version of the movie/episode. Unlinking the rest
        // before they are deleted keeps Jellyfin from saving the movie once per deleted row.
        LinkVersions(video, kept, ct);
        DeleteStreams(video, toDelete, ct);

        upsertedStreams.Add(video);

        stopwatch.Stop();

        _log.LogInformation(
            $"SyncStreams finished GelatoId={uri.ExternalId} userId={userId} duration={Math.Round(stopwatch.Elapsed.TotalSeconds, 1)}s streams={upsertedStreams.Count}"
        );

        return acceptable.Count;
    }

    /// <summary>
    /// Copies what a version's own page shows from its movie/episode. Jellyfin 12 clients load a
    /// version as the page item when it is picked, and Jellyfin does the same for local alternate
    /// versions in <see cref="Video.UpdateToRepositoryAsync"/>. Images, people and tags come from the
    /// movie at request time instead (<see cref="Decorators.DtoServiceDecorator"/>,
    /// <see cref="Filters.ImageResourceFilter"/>).
    /// </summary>
    private static void CopyVersionMetadata(Video primary, Video row)
    {
        row.Name = primary.Name;
        row.OriginalTitle = primary.OriginalTitle;
        row.Overview = primary.Overview;
        row.Tagline = primary.Tagline;
        row.Genres = primary.Genres;
        row.Studios = primary.Studios;
        row.ProductionLocations = primary.ProductionLocations;
        // No images of their own: Gelato downloads images per item on first view, so a copy would
        // be fetched again for every row and go stale when the movie's change.
        row.ImageInfos = [];
        row.ProductionYear = primary.ProductionYear;
        row.PremiereDate = primary.PremiereDate;
        row.EndDate = primary.EndDate;
        row.CommunityRating = primary.CommunityRating;
        row.CriticRating = primary.CriticRating;
        row.OfficialRating = primary.OfficialRating;
        row.CustomRating = primary.CustomRating;
        row.HomePageUrl = primary.HomePageUrl;
        row.RemoteTrailers = primary.RemoteTrailers;

        if (primary is Episode episode && row is Episode rowEpisode)
        {
            rowEpisode.SeriesName = episode.SeriesName;
            rowEpisode.SeasonName = episode.SeasonName;
            rowEpisode.IndexNumber = episode.IndexNumber;
            rowEpisode.ParentIndexNumber = episode.ParentIndexNumber;
        }
    }

    /// <summary>
    /// Makes the given stream rows the linked alternate versions of their movie/episode, in the
    /// order the addon returned them.
    /// </summary>
    private void LinkVersions(Video video, IReadOnlyCollection<Video> rows, CancellationToken ct)
    {
        // The links go onto the instance Jellyfin serves from its cache. A sync can run on a copy a
        // list query loaded, and the cached instance would keep its old links.
        var primary = libraryManager.GetItemById(video.Id) as Video ?? video;

        var rowIds = rows.Select(r => r.Id).ToHashSet();
        var linked = rows.Where(r => r.GelatoData<List<Guid>>("userIds") is { Count: > 0 })
            .OrderBy(r => r.GelatoData<int?>("index") ?? int.MaxValue)
            .Select(r => new LinkedChild
            {
                ItemId = r.Id,
                Type = MediaBrowser.Controller.Entities.LinkedChildType.LinkedAlternateVersion,
            });

        // Keep versions merged in by hand next to the streams. Read from the database, which the
        // links of every instance end up in.
        var stored = libraryManager.GetLinkedAlternateVersions(primary).ToList();
        var others = stored
            .Where(v => !rowIds.Contains(v.Id) && !v.HasStreamTag())
            .Select(v => new LinkedChild
            {
                ItemId = v.Id,
                Type = MediaBrowser.Controller.Entities.LinkedChildType.LinkedAlternateVersion,
            });

        LinkedChild[] links = [.. others, .. linked];
        var ids = links.Select(l => l.ItemId).ToList();
        if (
            ids.SequenceEqual(primary.LinkedAlternateVersions.Select(l => l.ItemId))
            && stored.Select(v => (Guid?)v.Id).ToHashSet().SetEquals(ids)
        )
        {
            return;
        }

        primary.LinkedAlternateVersions = links;
        if (!ReferenceEquals(primary, video))
        {
            video.LinkedAlternateVersions = links;
        }

        // Straight to the database: UpdateToRepositoryAsync would also run the metadata savers,
        // which write .nfo files next to a local movie's media.
        persistence.SaveItems([primary], ct);
    }

    /// <summary>Deletes only this primary item's linked or same-folder legacy rows.</summary>
    internal void DeleteOwnedStreamRows(Video primary)
    {
        var rows = libraryManager.GetLinkedAlternateVersions(primary)
            .Where(row => row.HasStreamTag() && row.PrimaryVersionId == primary.Id).ToList();
        if (primary.GetProviderId("Stremio") is { Length: > 0 } stremioId)
        {
            var legacy = repo.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [primary.GetBaseItemKind()],
                ParentId = primary.ParentId,
                Recursive = false,
                IncludeOwnedItems = true,
                IsDeadPerson = true,
                Tags = [StreamTag],
                HasAnyProviderId = new Dictionary<string, string> { ["Stremio"] = stremioId },
            }).OfType<Video>().Where(row => row.HasStreamTag() && row.PrimaryVersionId is null);
            rows.AddRange(legacy);
        }
        DeleteStreams(primary, rows.DistinctBy(row => row.Id).ToList(), CancellationToken.None);
    }

    internal void UnlinkStreamRow(Video row)
    {
        if (row.PrimaryVersionId is { } id && libraryManager.GetItemById(id) is Video primary)
        {
            var kept = libraryManager.GetLinkedAlternateVersions(primary)
                .Where(version => version.HasStreamTag() && version.Id != row.Id).ToList();
            LinkVersions(primary, kept, CancellationToken.None);
        }
        row.SetPrimaryVersionId(null);
        ForgetWatchState([row], CancellationToken.None);
    }

    /// <summary>
    /// Deletes stream rows no user has any more. Their watch state is already on the movie/episode
    /// (StreamUserDataSync).
    /// </summary>
    private void DeleteStreams(Video primary, IReadOnlyCollection<Video> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
            return;

        // Unlinked first: Jellyfin does not save the movie again for every row, and
        // StreamUserDataSync does not copy the cleared watch state to the movie.
        foreach (var row in rows)
        {
            row.SetPrimaryVersionId(null);
        }

        // Deleted items park their user data under their keys, which rows share with the movie.
        ForgetWatchState(rows, ct);

        foreach (var row in rows)
        {
            try
            {
                libraryManager.DeleteItem(
                    row,
                    new DeleteOptions { DeleteFileLocation = false },
                    false
                );
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to delete stale stream {Id}", row.Id);
            }
        }

        _log.LogDebug("Deleted {Count} stale stream(s) of {Id}", rows.Count, primary.Id);
    }

}
