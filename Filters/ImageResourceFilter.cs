using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

/// <summary>
/// Proxies image requests for search results (non-library gelato items), and serves a stream
/// row's images from its movie/episode. Library item images are handled by ImageProcessorDecorator.
/// </summary>
public sealed class ImageResourceFilter(
    IHttpClientFactory http,
    GelatoManager manager,
    ILibraryManager libraryManager,
    ILogger<ImageResourceFilter> log
) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext ctx,
        ResourceExecutionDelegate next
    )
    {
        if (
            ctx.ActionDescriptor
            is not ControllerActionDescriptor
            {
                ActionName: "GetItemImage"
                    or "GetItemImageByIndex"
                    or "GetItemImage2"
                    or "HeadItemImage"
                    or "HeadItemImageByIndex"
                    or "HeadItemImage2"
            }
        )
        {
            await next();
            return;
        }

        var routeValues = ctx.RouteData.Values;

        if (
            !routeValues.TryGetValue("itemId", out var guidString)
            || !Guid.TryParse(guidString?.ToString(), out var guid)
        )
        {
            await next();
            return;
        }

        // A stream row has no images of its own; the DTO hands out its movie's image tags.
        if (
            libraryManager.GetItemById(guid) is Video { PrimaryVersionId: { } primaryId } row
            && row.HasStreamTag()
        )
        {
            routeValues["itemId"] = primaryId.ToString("N");
            await next();
            return;
        }

        // Only handle cached search results — library items go through ProcessImage
        var url = manager.GetStremioMeta(guid)?.Poster;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)
            || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
        {
            await next();
            return;
        }

        log.LogDebug("ImageFilter: proxying search result item={ItemId}", guid);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.HttpContext.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var client = http.CreateClient(nameof(ImageResourceFilter));
            using var res = await client.GetAsync(
                target,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token
            );

            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning(
                    "ImageFilter: upstream returned {Status} for item={ItemId}",
                    res.StatusCode,
                    guid
                );
                await next();
                return;
            }

            var contentType = res.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (contentType is not ("image/jpeg" or "image/png" or "image/webp" or "image/gif" or "image/avif" or "image/bmp"))
            {
                await next();
                return;
            }
            await res.Content.LoadIntoBufferAsync(16 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
            ctx.HttpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.HttpContext.Response.ContentType = contentType;

            if (Microsoft.AspNetCore.Http.HttpMethods.IsHead(ctx.HttpContext.Request.Method)) return;
            await using var responseStream = await res.Content.ReadAsStreamAsync(
                timeout.Token
            );
            await responseStream.CopyToAsync(
                ctx.HttpContext.Response.Body,
                timeout.Token
            );
        }
        catch (OperationCanceledException) when (ctx.HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "ImageFilter: proxy failed for item={ItemId}", guid);
            if (ctx.HttpContext.Response.HasStarted) throw;
            await next();
        }
    }
}
