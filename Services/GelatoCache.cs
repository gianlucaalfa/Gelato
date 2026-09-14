using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace Gelato.Services;

/// <summary>A bounded cache owned by the plugin, independent of Jellyfin's shared cache.</summary>
public sealed class GelatoCache : IMemoryCache
{
    private readonly MemoryCache _inner = new(new MemoryCacheOptions { SizeLimit = 10000 });
    public ICacheEntry CreateEntry(object key)
    {
        var entry = _inner.CreateEntry(key);
        entry.Size = 1;
        return entry;
    }
    public bool TryGetValue(object key, out object? value) => _inner.TryGetValue(key, out value);
    public void Remove(object key) => _inner.Remove(key);
    public void Clear() => _inner.Clear();
    public void Dispose() => _inner.Dispose();
}
