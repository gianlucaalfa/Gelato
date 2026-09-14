using Xunit;

namespace Gelato.Tests;

public class ConcurrencyTests
{
    [Fact]
    public async Task WritesForOneItemNeverOverlap()
    {
        var gate = new KeyLock();
        var id = Guid.NewGuid();
        var active = 0;
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => gate.RunQueuedAsync(id, async ct =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            await Task.Yield();
            Interlocked.Decrement(ref active);
        })));
    }

    [Fact]
    public async Task CancellationDoesNotAbandonOtherWaiters()
    {
        var gate = new KeyLock();
        var id = Guid.NewGuid();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = gate.RunQueuedAsync(id, _ => held.Task);
        using var cancel = new CancellationTokenSource();
        var second = gate.RunQueuedAsync(id, _ => Task.CompletedTask, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        var thirdRan = false;
        var third = gate.RunQueuedAsync(id, _ => { thirdRan = true; return Task.CompletedTask; });
        Assert.False(thirdRan);
        held.SetResult();
        await Task.WhenAll(first, third);
        Assert.True(thirdRan);
    }

    [Fact]
    public async Task EquivalentReadsCoalesceButDifferentUsersDoNot()
    {
        var gate = new KeyLock();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task Work(CancellationToken _) { Interlocked.Increment(ref calls); return completion.Task; }
        var first = gate.RunSingleFlightAsync("user-a:item", Work);
        var same = gate.RunSingleFlightAsync("user-a:item", Work);
        var other = gate.RunSingleFlightAsync("user-b:item", Work);
        Assert.Same(first, same);
        Assert.Equal(2, calls);
        completion.SetResult();
        await Task.WhenAll(first, other);
    }
}
