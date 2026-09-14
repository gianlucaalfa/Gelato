using Microsoft.AspNetCore.Mvc;

namespace Gelato.Filters;

/// <summary>Streams upstream bytes and range metadata without buffering an entire media file.</summary>
public sealed class HttpStreamDownloadResult(IHttpClientFactory clients, Uri target) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var http = context.HttpContext;
        var ct = http.RequestAborted;
        using var client = clients.CreateClient(nameof(HttpStreamDownloadResult));
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        // Never forward Jellyfin authorization, cookies or arbitrary client headers upstream.
        foreach (var header in new[] { "Range", "If-Range" })
            if (http.Request.Headers.TryGetValue(header, out var value))
                request.Headers.TryAddWithoutValidation(header, value.ToArray());
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        http.Response.StatusCode = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            http.Response.StatusCode = 502;
            return;
        }
        http.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        http.Response.ContentLength = response.Content.Headers.ContentLength;
        var disposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
        disposition.FileNameStar = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "download";
        http.Response.Headers.ContentDisposition = disposition.ToString();
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        foreach (var header in new[] { "Content-Range" })
            if (response.Content.Headers.TryGetValues(header, out var values))
                http.Response.Headers[header] = string.Join(", ", values);
        foreach (var header in new[] { "Accept-Ranges", "ETag", "Last-Modified" })
            if (response.Headers.TryGetValues(header, out var values)
                || response.Content.Headers.TryGetValues(header, out values))
                http.Response.Headers[header] = string.Join(", ", values);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await stream.CopyToAsync(http.Response.Body, ct).ConfigureAwait(false);
    }
}
