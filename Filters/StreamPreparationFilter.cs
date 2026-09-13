using Gelato.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Gelato.Filters;

public sealed class StreamPreparationFilter(StreamSyncService sync, ILibraryManager library, IUserManager users)
    : IAsyncActionFilter, IOrderedFilter
{
    public int Order => 4;
    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        if (ctx.IsInsertableAction() && ctx.TryGetRouteGuid(out var id) && ctx.TryGetUserId(out var userId)
            && users.GetUserById(userId) is { } user && library.GetItemById<BaseItem>(id, user) is { } item)
            await sync.PrepareAsync(item, user, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
        await next();
    }
}
