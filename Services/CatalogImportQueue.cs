using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>Runs bounded manual imports independently of request lifetime and stops with Jellyfin.</summary>
public sealed class CatalogImportQueue(CatalogImportService imports, ILogger<CatalogImportQueue> log) : BackgroundService
{
    private readonly Channel<(string Id, string Type)> _queue = Channel.CreateBounded<(string, string)>(32);
    private readonly HashSet<(string, string)> _pending = [];
    private readonly Lock _gate = new();

    public bool TryQueue(string id, string type)
    {
        lock (_gate)
        {
            if (!_pending.Add((id, type))) return true;
            if (_queue.Writer.TryWrite((id, type))) return true;
            _pending.Remove((id, type));
            return false;
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try { await imports.ImportCatalogAsync(request.Id, request.Type, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Catalog import failed for {CatalogId}", request.Id); }
            finally { lock (_gate) { _pending.Remove(request); } }
        }
    }
}
