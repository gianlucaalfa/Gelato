namespace Gelato;

/// <summary>Serializes writes and coalesces equivalent reads without abandoning active waiters.</summary>
public sealed class KeyLock
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int References;
    }
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _queues = new();
    private readonly Dictionary<string, Task> _inflight = new(StringComparer.Ordinal);

    public Task RunSingleFlightAsync(Guid key, Func<CancellationToken, Task> action, CancellationToken ct = default) =>
        RunSingleFlightAsync(key.ToString("N"), action, ct);

    public Task RunSingleFlightAsync(string key, Func<CancellationToken, Task> action, CancellationToken ct = default)
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_inflight.TryGetValue(key, out var existing)) return existing;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _inflight.Add(key, completion.Task);
        }
        _ = CompleteAsync();
        return completion.Task;

        async Task CompleteAsync()
        {
            try { await action(ct).ConfigureAwait(false); completion.TrySetResult(); }
            catch (OperationCanceledException ex) { completion.TrySetCanceled(ex.CancellationToken); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { lock (_gate) { _inflight.Remove(key); } }
        }
    }

    public async Task RunQueuedAsync(Guid key, Func<CancellationToken, Task> action, CancellationToken ct = default)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_queues.TryGetValue(key, out entry!)) _queues[key] = entry = new();
            entry.References++;
        }
        var acquired = false;
        try
        {
            await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            await action(ct).ConfigureAwait(false);
        }
        finally
        {
            if (acquired) entry.Semaphore.Release();
            lock (_gate)
            {
                if (--entry.References == 0)
                {
                    _queues.Remove(key);
                    entry.Semaphore.Dispose();
                }
            }
        }
    }

    public async Task<T> RunQueuedAsync<T>(Guid key, Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
    {
        T result = default!;
        await RunQueuedAsync(key, async token => { result = await action(token).ConfigureAwait(false); }, ct).ConfigureAwait(false);
        return result;
    }
}
