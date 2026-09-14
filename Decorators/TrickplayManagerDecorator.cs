using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Configuration;

namespace Gelato.Decorators;

/// <summary>
/// Keeps Jellyfin's trickplay generation away from Gelato's items.
/// </summary>
/// <remarks>
/// Jellyfin 12's background generator expects local files and logs missing paths. Skip
/// Gelato remote versions without changing extraction for local media in mixed libraries.
/// </remarks>
public sealed class TrickplayManagerDecorator(ITrickplayManager inner) : ITrickplayManager
{
    public Task RefreshTrickplayDataAsync(
        Video video,
        bool replace,
        LibraryOptions libraryOptions,
        CancellationToken cancellationToken
    ) =>
        video.IsGelatoPlaybackItem()
            ? Task.CompletedTask
            : inner.RefreshTrickplayDataAsync(video, replace, libraryOptions, cancellationToken);

    public Task MoveGeneratedTrickplayDataAsync(
        Video video,
        LibraryOptions libraryOptions,
        CancellationToken cancellationToken
    ) =>
        video.IsGelatoPlaybackItem()
            ? Task.CompletedTask
            : inner.MoveGeneratedTrickplayDataAsync(video, libraryOptions, cancellationToken);

    public TrickplayInfo CreateTiles(
        IReadOnlyList<string> images,
        int width,
        TrickplayOptions options,
        string outputDir
    ) => inner.CreateTiles(images, width, options, outputDir);

    public Task<Dictionary<int, TrickplayInfo>> GetTrickplayResolutions(Guid itemId) =>
        inner.GetTrickplayResolutions(itemId);

    public Task<IReadOnlyList<TrickplayInfo>> GetTrickplayItemsAsync(int limit, int offset) =>
        inner.GetTrickplayItemsAsync(limit, offset);

    public Task SaveTrickplayInfo(TrickplayInfo info) => inner.SaveTrickplayInfo(info);

    public Task DeleteTrickplayDataAsync(Guid itemId, CancellationToken cancellationToken) =>
        inner.DeleteTrickplayDataAsync(itemId, cancellationToken);

    public Task<Dictionary<string, Dictionary<int, TrickplayInfo>>> GetTrickplayManifest(
        BaseItem item
    ) => inner.GetTrickplayManifest(item);

    public Task<string> GetTrickplayTilePathAsync(
        BaseItem item,
        int width,
        int index,
        bool saveWithMedia
    ) => inner.GetTrickplayTilePathAsync(item, width, index, saveWithMedia);

    public string GetTrickplayDirectory(
        BaseItem item,
        int tileWidth,
        int tileHeight,
        int width,
        bool saveWithMedia = false
    ) => inner.GetTrickplayDirectory(item, tileWidth, tileHeight, width, saveWithMedia);

    public Task<string?> GetHlsPlaylist(Guid itemId, int width, string? apiKey) =>
        inner.GetHlsPlaylist(itemId, width, apiKey);
}

