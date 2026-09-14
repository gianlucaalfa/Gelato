using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>Persistent, namespace-scoped Palco storage with atomic registration updates.</summary>
public sealed class PalcoCacheService : IDisposable
{
    private const string RegistrationNs = "anfiteatro-registration";
    private const string ProtectedPrefix = "gelato-protected:v1:";
    private readonly string _dbPath;
    private readonly IDataProtector _protector;
    private readonly SqliteConnection _connection;
    private readonly Lock _lock = new();
    private readonly Timer _cleanup;

    public PalcoCacheService(IApplicationPaths paths, ILogger<PalcoCacheService> logger, IDataProtectionProvider protection)
    {
        var directory = Path.Combine(paths.DataPath, "Palco");
        Directory.CreateDirectory(directory);
        _dbPath = Path.Combine(directory, "cache.db");
        _protector = protection.CreateProtector("Gelato.Palco.Smtp.v1");
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        _connection.Open();
        using var transaction = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS cache (key TEXT PRIMARY KEY, value TEXT NOT NULL, created_at INTEGER NOT NULL, expires_at INTEGER, namespace TEXT DEFAULT '')";
        cmd.ExecuteNonQuery();
        // Rebuild once, preserving the legacy database path and all surviving values.
        cmd.CommandText = "PRAGMA user_version";
        if (Convert.ToInt32(cmd.ExecuteScalar()) < 1)
        {
            cmd.CommandText = """
                CREATE TABLE cache_v1 (key TEXT NOT NULL, value TEXT NOT NULL, created_at INTEGER NOT NULL,
                    expires_at INTEGER, namespace TEXT NOT NULL DEFAULT '', PRIMARY KEY(namespace, key));
                INSERT INTO cache_v1 SELECT key, value, created_at, expires_at, COALESCE(namespace, '') FROM cache;
                DROP TABLE cache;
                ALTER TABLE cache_v1 RENAME TO cache;
                CREATE INDEX idx_cache_expiry ON cache(expires_at);
                PRAGMA user_version = 1;
                """;
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
        // Reads transparently migrate legacy plaintext SMTP settings.
        _ = Get("smtp-config", RegistrationNs);
        _cleanup = new Timer(_ =>
        {
            try { PurgeExpired(); }
            catch (Exception ex) { logger.LogWarning(ex, "Palco cache cleanup failed"); }
        }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public string? Get(string key, string ns = "")
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM cache WHERE key = @key AND namespace = @ns AND (expires_at IS NULL OR expires_at > @now)";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@ns", ns);
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return cmd.ExecuteScalar() is string value ? Decode(key, ns, value) : null;
        }
    }

    private string Decode(string key, string ns, string value)
    {
        if (key != "smtp-config" || ns != RegistrationNs)
            return value;
        if (value.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            return _protector.Unprotect(value[ProtectedPrefix.Length..]);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE cache SET value = @value WHERE key = @key AND namespace = @ns";
        cmd.Parameters.AddWithValue("@value", ProtectedPrefix + _protector.Protect(value));
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@ns", ns);
        cmd.ExecuteNonQuery();
        return value;
    }

    public void Set(string key, string value, int ttlSeconds = 0, string ns = "")
    {
        lock (_lock) { SetCore(key, value, ttlSeconds, ns); }
    }

    private void SetCore(string key, string value, int ttlSeconds, string ns, SqliteTransaction? transaction = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ttlSeconds);
        if (key.Length > 256 || ns.Length > 128 || value.Length > 262144)
            throw new ArgumentException("Cache entry exceeds the allowed size");
        if (key == "smtp-config" && ns == RegistrationNs)
            value = ProtectedPrefix + _protector.Protect(value);
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO cache(key,value,created_at,expires_at,namespace) VALUES(@key,@value,@now,@expiry,@ns) ON CONFLICT(namespace,key) DO UPDATE SET value=excluded.value,created_at=excluded.created_at,expires_at=excluded.expires_at";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@expiry", ttlSeconds > 0 ? now + ttlSeconds : DBNull.Value);
        cmd.Parameters.AddWithValue("@ns", ns);
        cmd.ExecuteNonQuery();
    }

    public bool TryAddRegistrationRequest(string key, string value, int ttlSeconds)
    {
        lock (_lock)
        {
            if (Get(key, RegistrationNs) is not null)
                return false;
            var index = JsonSerializer.Deserialize<List<string>>(Get("requests-index", RegistrationNs) ?? "[]") ?? [];
            index = index.Where(id => Get("request-" + id, RegistrationNs) is not null).Distinct().ToList();
            if (index.Count >= 1000)
                return false;
            index.Add(key["request-".Length..]);
            using var transaction = _connection.BeginTransaction();
            SetCore(key, value, ttlSeconds <= 0 ? 2592000 : Math.Clamp(ttlSeconds, 60, 2592000), RegistrationNs, transaction);
            SetCore("requests-index", JsonSerializer.Serialize(index), 0, RegistrationNs, transaction);
            transaction.Commit();
            return true;
        }
    }

    public bool Delete(string key, string ns = "")
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM cache WHERE key=@key AND namespace=@ns";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@ns", ns);
            var deleted = cmd.ExecuteNonQuery() > 0;
            if (key.StartsWith("request-", StringComparison.Ordinal) && ns == RegistrationNs)
            {
                cmd.CommandText = "SELECT value FROM cache WHERE key='requests-index' AND namespace=@ns";
                var index = JsonSerializer.Deserialize<List<string>>(cmd.ExecuteScalar() as string ?? "[]") ?? [];
                index.Remove(key["request-".Length..]);
                SetCore("requests-index", JsonSerializer.Serialize(index), 0, ns, transaction);
            }
            transaction.Commit();
            return deleted;
        }
    }

    public Dictionary<string, string> GetBulk(IEnumerable<string> keys, string ns = "")
    {
        var requested = keys.Distinct().Take(201).ToArray();
        if (requested.Length > 200) throw new ArgumentException("At most 200 keys are allowed");
        if (requested.Length == 0) return [];
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            var names = requested.Select((key, i) => "@k" + i).ToArray();
            cmd.CommandText = $"SELECT key,value FROM cache WHERE namespace=@ns AND key IN ({string.Join(',', names)}) AND (expires_at IS NULL OR expires_at>@now)";
            cmd.Parameters.AddWithValue("@ns", ns);
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            for (var i = 0; i < requested.Length; i++) cmd.Parameters.AddWithValue(names[i], requested[i]);
            var raw = new Dictionary<string, string>();
            using (var reader = cmd.ExecuteReader())
                while (reader.Read()) raw[reader.GetString(0)] = reader.GetString(1);
            return raw.ToDictionary(x => x.Key, x => Decode(x.Key, ns, x.Value));
        }
    }

    public void PurgeExpired()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM cache WHERE expires_at IS NOT NULL AND expires_at <= @now";
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
    }

    public (int total, int expired, long size) GetStats()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*),COALESCE(SUM(expires_at <= @now),0) FROM cache";
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return (reader.GetInt32(0), reader.GetInt32(1), new FileInfo(_dbPath).Length);
        }
    }

    public void Dispose()
    {
        _cleanup.Dispose();
        lock (_lock) { _connection.Dispose(); }
    }
}
