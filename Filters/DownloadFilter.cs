using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Gelato.Filters;

public sealed class DownloadFilter(
    ILibraryManager library,
    GelatoManager manager,
    IUserManager userManager,
    IMediaSourceManager mediaSourceManager,
    IHttpClientFactory httpClientFactory
) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        if (ctx.GetActionName() != "GetDownload" || !ctx.TryGetRouteGuid(out var guid))
        {
            await next();
            return;
        }

        var userIdStr = ctx
            .HttpContext.User.Claims.FirstOrDefault(c => c.Type is "UserId" or "Jellyfin-UserId")
            ?.Value;

        User? user = null;
        if (Guid.TryParse(userIdStr, out var userId))
        {
            user = userManager.GetUserById(userId);
        }

        if (user is not null)
        {
            var mediaSourceIdStr = ctx.HttpContext.Items["MediaSourceId"] as string;
            var hasMediaSourceId = Guid.TryParse(mediaSourceIdStr, out var mediaSourceId);

            var item = library.GetItemById<Video>(hasMediaSourceId ? mediaSourceId : guid, user);

            if (item != null && manager.IsStremio(item))
            {
                // Both the requested item and selected version must be visible to this user.
                if (library.GetItemById<Video>(guid, user) is not { } requested
                    || (item.IsStream() && (!(item.GelatoData<List<Guid>>("userIds")?.Contains(user.Id) ?? false)
                        || (item.Id != guid && item.PrimaryVersionId != (requested.PrimaryVersionId ?? requested.Id)))))
                {
                    ctx.Result = new NotFoundResult();
                    return;
                }
                var path = item.Path;
                if (!hasMediaSourceId || !item.IsStream())
                    path = mediaSourceManager.GetStaticMediaSources(item, true, user).FirstOrDefault()?.Path;
                if (!Uri.TryCreate(path, UriKind.Absolute, out var target)
                    || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
                {
                    ctx.Result = new NotFoundResult();
                    return;
                }
                ctx.Result = new HttpStreamDownloadResult(httpClientFactory, target);
                return;
            }
        }

        await next();
    }
}
