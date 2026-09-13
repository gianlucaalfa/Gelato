using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using MonoTorrent.Client;

namespace Gelato.Services;

/// <summary>Bounds torrent engines and owns cleanup for failures, response completion and shutdown.</summary>
public sealed class TorrentSessionService : IHostedService
{
    private readonly SemaphoreSlim _slots = new(4, 4);
    private readonly ConcurrentDictionary<Lease, byte> _active = new();
    private readonly CancellationTokenSource _stopping = new();

    public async Task<Lease> OpenAsync(EngineSettings settings, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
        await _slots.WaitAsync(linked.Token).ConfigureAwait(false);
        Lease? lease = null;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            lease = new Lease(new ClientEngine(settings), this);
            _active[lease] = 0;
            linked.Token.ThrowIfCancellationRequested();
            return lease;
        }
        catch
        {
            if (lease is null) _slots.Release();
            else await lease.DisposeAsync();
            throw;
        }
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken ct)
    {
        await _stopping.CancelAsync();
        foreach (var lease in _active.Keys) await lease.DisposeAsync();
    }

    public sealed class Lease(ClientEngine engine, TorrentSessionService owner) : IAsyncDisposable
    {
        private int _disposed;
        public ClientEngine Engine => engine;
        public TorrentManager? Manager { get; set; }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { if (Manager is not null) await Manager.StopAsync().ConfigureAwait(false); }
            finally
            {
                engine.Dispose();
                owner._active.TryRemove(this, out _);
                owner._slots.Release();
            }
        }
    }
}
