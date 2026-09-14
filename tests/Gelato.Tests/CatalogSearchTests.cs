using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public class CatalogSearchTests
{
    [Fact]
    public async Task HealthyCatalogSurvivesSiblingFailure()
    {
        var failures = new List<int>();
        var result = await CatalogSearch.CollectAsync<string>([
            _ => Task.FromException<IReadOnlyList<string>>(new HttpRequestException("Unavailable")),
            _ => Task.FromResult<IReadOnlyList<string>>(["series"])
        ], (index, _) => failures.Add(index), CancellationToken.None);
        Assert.Equal(["series"], result);
        Assert.Equal([0], failures);
    }

    [Fact]
    public async Task TotalOutageIsNotReportedAsAnEmptySearch()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => CatalogSearch.CollectAsync<string>([
            _ => throw new IOException("Unavailable"),
            _ => Task.FromException<IReadOnlyList<string>>(new HttpRequestException("Unavailable"))
        ], (_, _) => { }, CancellationToken.None));
    }

    [Fact]
    public async Task SuccessfulEmptyCatalogRemainsAnEmptyResult()
    {
        var result = await CatalogSearch.CollectAsync<string>([
            _ => Task.FromResult<IReadOnlyList<string>>([]),
            _ => Task.FromException<IReadOnlyList<string>>(new HttpRequestException("Unavailable"))
        ], (_, _) => { }, CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchesRunConcurrentlyAndPreserveCatalogOrder()
    {
        var first = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var pending = CatalogSearch.CollectAsync<string>([
            _ => { Interlocked.Increment(ref started); return first.Task; },
            _ => { Interlocked.Increment(ref started); return second.Task; }
        ], (_, _) => { }, CancellationToken.None);
        Assert.Equal(2, started);
        second.SetResult(["series"]);
        first.SetResult(["movie"]);
        Assert.Equal(["movie", "series"], await pending);
    }

    [Fact]
    public async Task RequestCancellationIsNotSwallowed()
    {
        using var cancellation = new CancellationTokenSource();
        var failures = 0;
        var pending = CatalogSearch.CollectAsync<string>([
            async ct => { await Task.Delay(Timeout.Infinite, ct); return []; },
            _ => Task.FromResult<IReadOnlyList<string>>(["series"])
        ], (_, _) => Interlocked.Increment(ref failures), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, failures);
    }
}
