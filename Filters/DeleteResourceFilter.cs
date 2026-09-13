using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

public sealed class DeleteResourceFilter(
    ILibraryManager library,
    GelatoManager manager,
    IUserManager userManager,
    ILogger<DeleteResourceFilter> log
) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        // Only intercept DeleteItem actions with valid user
        if (
            ctx.GetActionName() != "DeleteItem"
            || !ctx.TryGetRouteGuid(out var guid)
            || !ctx.TryGetUserId(out var userId)
            || userManager.GetUserById(userId) is not { } user
        )
        {
            await next();
            return;
        }

        var item = library.GetItemById<BaseItem>(guid, user);

        // Only handle Gelato items that user can delete
        if (item is null || !item.IsGelato() || !manager.CanDelete(item, user))
        {
            await next();
            return;
        }

        var owner = (item as Video)?.PrimaryVersionId ?? item.Id;
        await manager.RunStreamMutationAsync(owner, _ =>
        {
            // Recheck inside the lock: a queued deletion may already have removed the item.
            var current = library.GetItemById<BaseItem>(guid, user);
            if (current is null || !manager.CanDelete(current, user)) return Task.CompletedTask;
            if (current is Video video && current.IsPrimaryVersion())
            {
                manager.DeleteOwnedStreamRows(video);
            }
            else if (current is Video row && current.HasStreamTag())
            {
                manager.UnlinkStreamRow(row);
            }
            log.LogInformation("Deleting {Name} ({Id})", current.Name, current.Id);
            library.DeleteItem(current, new DeleteOptions { DeleteFileLocation = false }, true);
            manager.ClearCache();
            return Task.CompletedTask;
        }, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
        ctx.Result = new NoContentResult();
    }
}
