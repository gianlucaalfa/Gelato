using Gelato.Decorators;
using Gelato.Services;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Gelato.Tests;

public class RemoteHardeningTests
{
    [Theory]
    [InlineData("https://user:password@example.test/private-key/file?token=query-secret#fragment")]
    [InlineData("HTTP://localhost:3066/stremio/private-key/manifest.json")]
    [InlineData("https%3A%2F%2Fexample.test%2Fprivate-key%3Ftoken%3Dquery-secret")]
    [InlineData("https:\\/\\/example.test/private-key")]
    [InlineData("gelato://private-key")]
    public void PluginLogProvidersNeverReceiveUrlStateOrExceptions(string url)
    {
        var sink = new CaptureLogger();
        var factory = new Mock<ILoggerFactory>();
        factory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(sink);
        var logger = new RedactingLogger<RemoteHardeningTests>(factory.Object);
        var error = new HttpRequestException("Bearer unstructured-secret " + url,
            new IOException("nested-secret"));
        using (logger.BeginScope("Scope " + url))
            logger.LogWarning(error, "Item {Id} failed at {Url}", 42, url);
        Assert.Contains("Item 42", sink.Message);
        Assert.Contains(nameof(HttpRequestException), sink.Message);
        Assert.Contains("[redacted-url]", sink.Message);
        Assert.DoesNotContain("private-key", sink.Message + sink.State + sink.Scope);
        Assert.DoesNotContain("query-secret", sink.Message + sink.State + sink.Scope);
        Assert.DoesNotContain("unstructured-secret", sink.Message + sink.State + sink.Scope);
        Assert.Null(sink.Exception);
    }

    [Fact]
    public void RedactionRegistrationOnlyReplacesPluginLoggers()
    {
        var services = new ServiceCollection().AddLogging();
        SafeLog.Register(services);
        using var provider = services.BuildServiceProvider();
        Assert.IsType<RedactingLogger<ProviderManagerDecorator>>(provider.GetRequiredService<ILogger<ProviderManagerDecorator>>());
        Assert.IsType<RedactingLogger<GelatoPlugin>>(provider.GetRequiredService<ILogger<GelatoPlugin>>());
        Assert.IsNotType<RedactingLogger<RemoteHardeningTests>>(provider.GetRequiredService<ILogger<RemoteHardeningTests>>());
    }

    [Theory]
    [InlineData("https://example.test/authenticated", true, false)]
    [InlineData("gelato://placeholder", false, false)]
    [InlineData("/media/movie.mkv", false, true)]
    public async Task BackgroundImageTasksSkipOnlyGelatoRemoteItems(string path, bool tagged, bool extract)
    {
        var video = new Movie { Path = path, Tags = tagged ? [GelatoManager.StreamTag] : [] };
        // Local files in mixed libraries can also have Stremio metadata.
        video.SetProviderId("Stremio", "tt1234567");
        var directory = Mock.Of<IDirectoryService>();
        IReadOnlyList<ChapterInfo> chapters = [new ChapterInfo { StartPositionTicks = 10 }];
        using var cancellation = new CancellationTokenSource();
        var chapterManager = new Mock<IChapterManager>(MockBehavior.Strict);
        chapterManager.Setup(x => x.RefreshChapterImages(video, directory, chapters, extract, true, cancellation.Token))
            .ReturnsAsync(true);
        Assert.True(await new ChapterManagerDecorator(chapterManager.Object)
            .RefreshChapterImages(video, directory, chapters, true, true, cancellation.Token));
        chapterManager.VerifyAll();
        var options = new LibraryOptions { EnableChapterImageExtraction = true };
        var trickplay = new Mock<ITrickplayManager>(MockBehavior.Strict);
        if (extract)
        {
            trickplay.Setup(x => x.RefreshTrickplayDataAsync(video, true, options, cancellation.Token)).Returns(Task.CompletedTask);
            trickplay.Setup(x => x.MoveGeneratedTrickplayDataAsync(video, options, cancellation.Token)).Returns(Task.CompletedTask);
        }
        var decorator = new TrickplayManagerDecorator(trickplay.Object);
        await decorator.RefreshTrickplayDataAsync(video, true, options, cancellation.Token);
        await decorator.MoveGeneratedTrickplayDataAsync(video, options, cancellation.Token);
        trickplay.VerifyAll();
        Assert.Equal(extract ? 2 : 0, trickplay.Invocations.Count);
    }

    [Fact]
    public async Task ImageSidecarsRemainCompleteDuringConcurrentWritesAndPreserveImages()
    {
        var directory = Directory.CreateTempSubdirectory("gelato-image-test-");
        try
        {
            var path = Path.Combine(directory.FullName, "Primary.jpg");
            byte[] original = [1, 2, 3, 4];
            File.WriteAllBytes(path, original);
            var urls = Enumerable.Range(0, 24).Select(i => "https://example.test/" + i + "/" + new string('x', 8192)).ToArray();
            RemoteImageFiles.WriteUrl(path, urls[0]);
            using var stop = new CancellationTokenSource();
            var reader = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    Assert.Contains(await File.ReadAllTextAsync(path + ".url"), urls);
                    await Task.Yield();
                }
            });
            try
            {
                await Task.WhenAll(urls.Select(url => Task.Run(() =>
                {
                    RemoteImageFiles.EnsurePlaceholder(path);
                    RemoteImageFiles.WriteUrl(path, url);
                })));
            }
            finally { stop.Cancel(); await reader; }
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Contains(File.ReadAllText(path + ".url"), urls);
            Assert.Empty(Directory.GetFiles(directory.FullName, "*.tmp"));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void ExistingReadOnlyImagesDoNotRequireWriteAccess()
    {
        var directory = Directory.CreateTempSubdirectory("gelato-readonly-image-");
        var path = Path.Combine(directory.FullName, "Primary.jpg");
        try
        {
            File.WriteAllBytes(path, [1, 2, 3]);
            File.SetAttributes(path, FileAttributes.ReadOnly);
            RemoteImageFiles.EnsurePlaceholder(path);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            directory.Delete(true);
        }
    }

    [Fact]
    public void ImageSidecarErrorsAreNotSilentlyIgnored()
    {
        var directory = Directory.CreateTempSubdirectory("gelato-image-error-");
        try
        {
            var path = Path.Combine(directory.FullName, "Primary.jpg");
            Directory.CreateDirectory(path + ".url");
            Assert.ThrowsAny<IOException>(() => RemoteImageFiles.WriteUrl(path, "https://example.test/image"));
            Assert.Empty(Directory.GetFiles(directory.FullName, "*.tmp"));
        }
        finally { directory.Delete(true); }
    }

    private sealed class CaptureLogger : ILogger
    {
        public string Message = "";
        public string State = "";
        public string Scope = "";
        public Exception? Exception;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            Scope = state.ToString()!;
            return null;
        }
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Message = formatter(state, exception);
            State = state?.ToString() ?? "";
            Exception = exception;
        }
    }
}
