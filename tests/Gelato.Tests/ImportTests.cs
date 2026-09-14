using Gelato.Config;
using Gelato.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gelato.Tests;

public class ImportTests
{
    [Fact]
    public void ManualQueueCoalescesDuplicatesAndRejectsOverflow()
    {
        using var queue = new CatalogImportQueue(null!, NullLogger<CatalogImportQueue>.Instance);
        for (var i = 0; i < 32; i++) Assert.True(queue.TryQueue(i.ToString(), "movie"));
        Assert.True(queue.TryQueue("0", "movie"));
        Assert.False(queue.TryQueue("overflow", "movie"));
    }

    [Theory]
    [InlineData(0, 150, 150)]
    [InlineData(30, 150, 30)]
    [InlineData(0, 0, 1)]
    [InlineData(50000, 150, 10000)]
    public void ItemLimitUsesGlobalFallbackAndStaysBounded(int local, int global, int expected) =>
        Assert.Equal(expected, new CatalogConfig { MaxItems = local }.EffectiveMaxItems(global));
}
