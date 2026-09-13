using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Gelato.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Gelato.Tests;

public class HttpStreamingTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(response(request));
        }
    }

    private static IHttpClientFactory Factory(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));
        return factory.Object;
    }

    [Fact]
    public async Task ResumedDownloadPreservesRangeWithoutForwardingCredentials()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal("bytes=10-12", request.Headers.Range!.ToString());
            Assert.Equal("\"version\"", request.Headers.IfRange!.ToString());
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StringContent("abc") };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(10, 12, 100);
            response.Headers.AcceptRanges.Add("bytes");
            return response;
        });
        var context = new DefaultHttpContext();
        context.Request.Headers.Range = "bytes=10-12";
        context.Request.Headers.IfRange = "\"version\"";
        context.Request.Headers.Authorization = "Bearer jellyfin-secret";
        context.Request.Headers.Cookie = "session=private";
        context.Response.Body = new MemoryStream();
        await new HttpStreamDownloadResult(Factory(handler), new Uri("https://example.test/video"))
            .ExecuteResultAsync(new ActionContext(context, new RouteData(), new ActionDescriptor()));
        Assert.Equal(206, context.Response.StatusCode);
        Assert.Equal("bytes 10-12/100", context.Response.Headers.ContentRange.ToString());
        Assert.Equal("attachment; filename*=utf-8''download", context.Response.Headers.ContentDisposition.ToString());
        Assert.Equal("abc", Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }

    [Fact]
    public async Task UnsatisfiableRangeRetainsTheUpstreamSize()
    {
        using var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent([]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(100);
            return response;
        });
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await new HttpStreamDownloadResult(Factory(handler), new Uri("https://example.test/video"))
            .ExecuteResultAsync(new ActionContext(context, new RouteData(), new ActionDescriptor()));
        Assert.Equal(416, context.Response.StatusCode);
        Assert.Equal("bytes */100", context.Response.Headers.ContentRange.ToString());
    }

    [Fact]
    public async Task MetadataCacheSeparatesMoviesAndSeriesWithTheSameId()
    {
        var calls = 0;
        using var handler = new Handler(request =>
        {
            calls++;
            var type = request.RequestUri!.AbsolutePath.Contains("/movie/") ? "movie" : "series";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"meta":{"id":"same","type":"{{type}}","name":"{{type}}"}}""") };
        });
        var provider = new GelatoStremioProvider("https://example.test/config", Factory(handler), NullLogger<GelatoStremioProvider>.Instance);
        Assert.Equal("movie", (await provider.GetMetaAsync("same", StremioMediaType.Movie))!.Name);
        Assert.Equal("series", (await provider.GetMetaAsync("same", StremioMediaType.Series))!.Name);
        await provider.GetMetaAsync("same", StremioMediaType.Movie);
        Assert.Equal(2, calls);
        provider.ClearCache();
        await provider.GetMetaAsync("same", StremioMediaType.Movie);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("ftp://example.test/video", false)]
    [InlineData("https://example.test/", true)]
    [InlineData("http://127.0.0.1:3000/stream/token", true)]
    [InlineData("https://cdn.example.test/video?token=secret", true)]
    public void StreamsAcceptOnlyHttpProtocolsWithoutBreakingLocalAddons(string url, bool expected) =>
        Assert.Equal(expected, new StremioStream { Url = url }.IsValid());
}
