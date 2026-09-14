namespace Gelato.Services;

/// <summary>Bounds anonymous registration work without requiring host middleware changes.</summary>
public sealed class RegistrationRequestLimiter
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _clients = new();
    private DateTimeOffset _window = DateTimeOffset.UtcNow;
    private int _total;

    public static bool IsRegistrationEnabled(string? value) =>
        string.Equals(value?.Trim(), "true", StringComparison.Ordinal);

    public bool TryAcquire(string client)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _window >= TimeSpan.FromMinutes(1))
            {
                _window = now;
                _total = 0;
                foreach (var key in _clients.Where(x => now - x.Value.Start >= TimeSpan.FromMinutes(10)).Select(x => x.Key).ToArray())
                    _clients.Remove(key);
            }
            var entry = _clients.GetValueOrDefault(client, (Start: now, Count: 0));
            if (_total >= 30 || entry.Count >= 5)
                return false;
            _clients[client] = (entry.Start, entry.Count + 1);
            _total++;
            return true;
        }
    }
}
