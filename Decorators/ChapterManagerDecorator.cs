using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace Gelato.Decorators;

/// <summary>
/// Keeps Jellyfin's chapter image extraction away from Gelato's items.
/// </summary>
/// <remarks>
/// Remote HTTP versions must not trigger background chapter extraction or persist their
/// authenticated URLs in chapter-failure files. Cleanup and local media still pass through.
/// </remarks>
public sealed class ChapterManagerDecorator(IChapterManager inner) : IChapterManager
{
    public bool Supports(BaseItem item) => inner.Supports(item);

    public void SaveChapters(BaseItem item, IReadOnlyList<ChapterInfo> chapters) =>
        inner.SaveChapters(item, chapters);

    public ChapterInfo? GetChapter(Guid baseItemId, int index) =>
        inner.GetChapter(baseItemId, index);

    public IReadOnlyList<ChapterInfo> GetChapters(Guid baseItemId) => inner.GetChapters(baseItemId);

    public Task<bool> RefreshChapterImages(
        Video video,
        IDirectoryService directoryService,
        IReadOnlyList<ChapterInfo> chapters,
        bool extractImages,
        bool saveChapters,
        CancellationToken cancellationToken
    ) =>
        inner.RefreshChapterImages(
            video,
            directoryService,
            chapters,
            extractImages && !video.IsGelatoPlaybackItem(),
            saveChapters,
            cancellationToken
        );

    public Task DeleteChapterDataAsync(Guid itemId, CancellationToken cancellationToken) =>
        inner.DeleteChapterDataAsync(itemId, cancellationToken);
}

