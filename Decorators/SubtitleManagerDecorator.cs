#nullable disable
#pragma warning disable CS1591

using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators
{
    public sealed class SubtitleManagerDecorator : ISubtitleManager
    {
        private readonly ISubtitleManager _inner;
        private readonly ILogger<SubtitleManagerDecorator> _log;
        private readonly Lazy<ILibraryManager> _libraryManager;
        private readonly ILocalizationManager _localization;

        public SubtitleManagerDecorator(
            ISubtitleManager inner,
            ILogger<SubtitleManagerDecorator> log,
            Lazy<ILibraryManager> libraryManager,
            ILocalizationManager localization
        )
        {
            _inner = inner;
            _log = log;
            _libraryManager = libraryManager;
            _localization = localization;
        }

        public event EventHandler<SubtitleDownloadFailureEventArgs> SubtitleDownloadFailure
        {
            add => _inner.SubtitleDownloadFailure += value;
            remove => _inner.SubtitleDownloadFailure -= value;
        }

        public Task<RemoteSubtitleInfo[]> SearchSubtitles(
            Video video,
            string language,
            bool? isPerfectMatch,
            bool isAutomated,
            CancellationToken cancellationToken
        ) =>
            _inner.SearchSubtitles(video, language, isPerfectMatch, isAutomated, cancellationToken);

        public Task<RemoteSubtitleInfo[]> SearchSubtitles(
            SubtitleSearchRequest request,
            CancellationToken cancellationToken
        )
        {
            // nasty hack to prevent some plugins chocking on remote files
            // request.MediaPath = request.MediaPath + ".strm";
            if (request.IsAutomated && SkipAutomatedSearch(request))
                return Task.FromResult(Array.Empty<RemoteSubtitleInfo>());

            return _inner.SearchSubtitles(request, cancellationToken);
        }

        /// <summary>
        /// The "Download missing subtitles" task picks every video without an external subtitle
        /// stream in the database. Jellyfin only records those for local files, so every Gelato item
        /// looks like it is missing one, and the task fetched the same subtitle again on each run.
        /// SubtitleManager never overwrites, it saves the next copy as .en.0.vtt, .en.1.vtt, …
        /// </summary>
        private bool SkipAutomatedSearch(SubtitleSearchRequest request)
        {
            var path = request.MediaPath;
            if (string.IsNullOrEmpty(path))
                return false;

            // Placeholders (gelato://stub/…) are not a release. Playback only offers subtitles
            // saved for stream items, so anything downloaded for a placeholder is never used.
            if (path.StartsWith("gelato://", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogDebug("Skipping automated subtitle search for placeholder {Path}", path);
                return true;
            }

            // Stream items. Probing runs under a local /tmp/<release>.strm path, where Jellyfin
            // finds the saved files itself.
            if (!path.IsUrl())
                return false;

            var item = _libraryManager
                .Value.GetItemList(new InternalItemsQuery { Path = path, IncludeOwnedItems = true })
                .FirstOrDefault();
            if (item is null || !item.IsGelato())
                return false;

            var language = NormalizeLanguage(request.Language);
            if (
                item.GetGelatoSubtitleFiles()
                    .Any(f =>
                        string.Equals(
                            NormalizeLanguage(f.Language),
                            language,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
            )
            {
                _log.LogDebug(
                    "Skipping automated subtitle search for {Id}, a {Language} subtitle is already saved",
                    item.Id,
                    request.Language
                );
                return true;
            }

            return false;
        }

        // Library options use three-letter codes (eng), saved files the addon's code (en).
        private string NormalizeLanguage(string language) =>
            string.IsNullOrEmpty(language)
                ? language
                : _localization.FindLanguageInfo(language)?.TwoLetterISOLanguageName ?? language;

        public Task DownloadSubtitles(
            Video video,
            string subtitleId,
            CancellationToken cancellationToken
        )
        {
            // This is the overload Jellyfin calls (the scheduled task, metadata refresh and the
            // subtitle API). The inner one loads the library options and saves on its own, so
            // route Gelato items through the overload below.
            if (video.IsGelato())
            {
                return DownloadSubtitles(
                    video,
                    _libraryManager.Value.GetLibraryOptions(video),
                    subtitleId,
                    cancellationToken
                );
            }

            return _inner.DownloadSubtitles(video, subtitleId, cancellationToken);
        }

        public async Task DownloadSubtitles(
            Video video,
            LibraryOptions libraryOptions,
            string subtitleId,
            CancellationToken cancellationToken
        )
        {
            if (video.IsGelato())
            {
                // A Gelato item has no folder to save next to. With the filename swap below the
                // "media folder" would be the server's working directory, and for a placeholder the
                // path is a URL. Save to the item's metadata folder, which is where
                // GetGelatoSubtitleFiles looks. Copy the options: GetLibraryOptions returns the
                // instance Jellyfin caches for the whole library.
                if (libraryOptions.SaveSubtitlesWithMedia)
                {
                    libraryOptions = JsonSerializer.Deserialize<LibraryOptions>(
                        JsonSerializer.Serialize(libraryOptions)
                    );
                    libraryOptions.SaveSubtitlesWithMedia = false;
                }

                // Jellyfin derives the subtitle save filename from video.Path.
                // For gelato stream items the path is a URL, which produces garbage names.
                // Use the BehaviorHints.Filename stored in GelatoData if available.
                var gelatoFilename = video.GelatoData<string>("filename");
                if (!string.IsNullOrEmpty(gelatoFilename))
                {
                    var originalPath = video.Path;
                    video.Path = gelatoFilename;
                    try
                    {
                        await _inner
                            .DownloadSubtitles(video, libraryOptions, subtitleId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        video.Path = originalPath;
                    }
                    return;
                }
            }

            await _inner
                .DownloadSubtitles(video, libraryOptions, subtitleId, cancellationToken)
                .ConfigureAwait(false);
        }

        public Task UploadSubtitle(Video video, SubtitleResponse response) =>
            _inner.UploadSubtitle(video, response);

        public Task<SubtitleResponse> GetRemoteSubtitles(
            string id,
            CancellationToken cancellationToken
        ) => _inner.GetRemoteSubtitles(id, cancellationToken);

        public Task DeleteSubtitles(BaseItem item, int index) =>
            _inner.DeleteSubtitles(item, index);

        public SubtitleProviderInfo[] GetSupportedProviders(BaseItem item) =>
            _inner.GetSupportedProviders(item);
    }
}
