using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Gelato.Services;

/// <summary>Authenticates internal FFmpeg requests without exposing a Jellyfin user token.</summary>
public sealed class TorrentAccess(IDataProtectionProvider protection)
{
    private readonly IDataProtector _protector = protection.CreateProtector("Gelato.TorrentAccess.v1");
    public string Create(string hash, int? index, string? trackers) =>
        _protector.Protect(JsonSerializer.Serialize(new Request(hash, index, trackers)));
    public bool Validate(string? token, string hash, int? index, string? trackers)
    {
        if (string.IsNullOrEmpty(token)) return false;
        try { return JsonSerializer.Deserialize<Request>(_protector.Unprotect(token)) == new Request(hash, index, trackers); }
        catch (CryptographicException) { return false; }
        catch (JsonException) { return false; }
    }
    private sealed record Request(string Hash, int? Index, string? Trackers);
}
