using Gelato.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Gelato.Tests;

public class StorageTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "gelato-tests-" + Guid.NewGuid());
    private PalcoCacheService Create()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(x => x.DataPath).Returns(_path);
        return new(paths.Object, NullLogger<PalcoCacheService>.Instance, new EphemeralDataProtectionProvider());
    }

    [Fact]
    public void NamespacesDoNotOverwriteEachOther()
    {
        using var cache = Create();
        cache.Set("same", "first", ns: "a");
        cache.Set("same", "second", ns: "b");
        Assert.Equal("first", cache.Get("same", "a"));
        Assert.Equal("second", cache.GetBulk(["same"], "b")["same"]);
    }

    [Fact]
    public void LegacyRowsSurviveMigrationAndSmtpIsProtectedOnDisk()
    {
        Directory.CreateDirectory(Path.Combine(_path, "Palco"));
        var file = Path.Combine(_path, "Palco", "cache.db");
        using (var db = new SqliteConnection("Data Source=" + file))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TABLE cache(key TEXT PRIMARY KEY,value TEXT NOT NULL,created_at INTEGER NOT NULL,expires_at INTEGER,namespace TEXT DEFAULT ''); INSERT INTO cache VALUES('legacy','retained',0,NULL,'a'); INSERT INTO cache VALUES('smtp-config','secret',0,NULL,'anfiteatro-registration');";
            cmd.ExecuteNonQuery();
        }
        using var cache = Create();
        Assert.Equal("retained", cache.Get("legacy", "a"));
        Assert.Equal("secret", cache.Get("smtp-config", "anfiteatro-registration"));
        using var connection = new SqliteConnection("Data Source=" + file);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM cache WHERE key='smtp-config'";
        Assert.StartsWith("gelato-protected:v1:", (string)command.ExecuteScalar()!);
    }

    [Fact]
    public async Task ConcurrentRegistrationsKeepEveryIndexEntry()
    {
        using var cache = Create();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
            Assert.True(cache.TryAddRegistrationRequest("request-" + i, "{}", 60)))));
        var entries = System.Text.Json.JsonSerializer.Deserialize<string[]>(cache.Get("requests-index", "anfiteatro-registration")!);
        Assert.Equal(20, entries!.Length);
        Assert.False(cache.TryAddRegistrationRequest("request-0", "{}", 60));
        cache.Delete("request-0", "anfiteatro-registration");
        Assert.DoesNotContain("0", System.Text.Json.JsonSerializer.Deserialize<string[]>(cache.Get("requests-index", "anfiteatro-registration")!)!);
    }

    [Fact]
    public void PluginInvalidationDoesNotEvictHostEntries()
    {
        using var host = new MemoryCache(new MemoryCacheOptions());
        using var plugin = new GelatoCache();
        host.Set("host", 42);
        plugin.Set("plugin", 43);
        plugin.Clear();
        Assert.Equal(42, host.Get<int>("host"));
        Assert.False(plugin.TryGetValue("plugin", out _));
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_path)) Directory.Delete(_path, true); }
}
