using Gelato.Config;
using MediaBrowser.Common.Api;
using Gelato.ScheduledTasks;
using Gelato.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Gelato.Controllers;

[ApiController]
[Route("gelato/catalogs")]
[Authorize(Policy = Policies.RequiresElevation)]
public class CatalogController(
    ILogger<CatalogController> logger,
    CatalogService catalogService,
    CatalogImportQueue importQueue,
    ITaskManager taskManager,
    ILibraryManager libraryManager
) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<CatalogConfig>>> GetCatalogs()
    {
        // Use Global user for now, or HttpContext.User if we want per-user catalogs later
        // But CatalogService currently uses Guid.Empty for global config if passed
        // We'll stick to global administration for now as per plan
        return await catalogService.GetCatalogsAsync(Guid.Empty);
    }

    [HttpPost("{id}/{type}/config")]
    public ActionResult UpdateConfig(
        [FromRoute] string id,
        [FromRoute] string type,
        [FromBody] CatalogConfig config
    )
    {
        if (config.Id != id || config.Type != type)
        {
            return BadRequest("ID/Type mismatch");
        }

        catalogService.UpdateCatalogConfig(config);
        return Ok();
    }

    [HttpPost("{id}/{type}/import")]
    public Task<ActionResult> TriggerImport([FromRoute] string id, [FromRoute] string type)
    {
        var config = catalogService.GetCatalogConfig(id, type);
        if (config is null) return Task.FromResult<ActionResult>(NotFound());
        if (!config.Enabled) return Task.FromResult<ActionResult>(BadRequest("Catalog is disabled"));
        return Task.FromResult<ActionResult>(importQueue.TryQueue(id, type)
            ? Accepted() : StatusCode(429, "The import queue is full"));
    }

    [HttpPost("import-all")]
    public ActionResult ImportAll()
    {
        logger.LogInformation("Manual import triggered for all enabled catalogs");

        taskManager.QueueScheduledTask<GelatoCatalogItemsSyncTask>();
        return Accepted();
    }
}
