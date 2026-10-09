using CoffeeNChill.Functions.Helpers;
using CoffeeNChill.Functions.Models;
using CoffeeNChill.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CoffeeNChill.Functions.Functions;

/// <summary>
/// CRUD HTTP functions for the digital menu (Azure Table "MenuItems").
/// Status codes: 201 created, 200 ok, 204 deleted, 400 validation, 404 missing, 409 duplicate/concurrency.
/// </summary>
public class MenuFunctions
{
    private readonly IMenuRepository _menu;
    private readonly ILogger<MenuFunctions> _logger;

    public MenuFunctions(IMenuRepository menu, ILogger<MenuFunctions> logger)
    {
        _menu = menu;
        _logger = logger;
    }

    /// <summary>POST /api/menu - inserts a new menu entity.</summary>
    [Function("CreateMenuItem")]
    public Task<IActionResult> CreateMenuItem(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "menu")] HttpRequest req,
        CancellationToken ct) =>
        ApiResults.GuardAsync(_logger, async () =>
        {
            var (body, error) = await RequestReader.ReadJsonAsync<CreateMenuItemRequest>(req, ct);
            if (error is not null) return ApiResults.BadRequest(error);

            var errors = ModelValidator.Validate(body!);
            if (errors.Count > 0) return ApiResults.ValidationFailed(errors);

            var entity = body!.ToEntity();
            if (!await _menu.TryAddAsync(entity, ct))
                return ApiResults.Conflict($"Menu item '{entity.RowKey}' already exists in category '{entity.PartitionKey}'.");

            _logger.LogInformation("Created menu item {Sku} in {Category}", entity.RowKey, entity.PartitionKey);
            var location = $"/api/menu/{Uri.EscapeDataString(entity.PartitionKey)}/{Uri.EscapeDataString(entity.RowKey)}";
            return new CreatedResult(location, MenuItemResponse.From(entity));
        });

    /// <summary>GET /api/menu - returns every menu entity.</summary>
    [Function("GetAllMenuItems")]
    public Task<IActionResult> GetAllMenuItems(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "menu")] HttpRequest req,
        CancellationToken ct) =>
        ApiResults.GuardAsync(_logger, async () =>
        {
            var items = await _menu.GetAllAsync(ct);
            return new OkObjectResult(items.Select(MenuItemResponse.From).ToList());
        });

    /// <summary>GET /api/menu/category/{category} - filters by PartitionKey. Unknown category returns an empty list.</summary>
    [Function("GetMenuItemsByCategory")]
    public Task<IActionResult> GetMenuItemsByCategory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "menu/category/{category}")] HttpRequest req,
        string category,
        CancellationToken ct) =>
        ApiResults.GuardAsync(_logger, async () =>
        {
            if (string.IsNullOrWhiteSpace(category)) return ApiResults.BadRequest("Category is required.");

            var items = await _menu.GetByCategoryAsync(category.Trim(), ct);
            return new OkObjectResult(items.Select(MenuItemResponse.From).ToList());
        });

    /// <summary>PUT /api/menu/{category}/{id} - updates price, availability (and optionally name/description).</summary>
    [Function("UpdateMenuItem")]
    public Task<IActionResult> UpdateMenuItem(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "menu/{category}/{id}")] HttpRequest req,
        string category,
        string id,
        CancellationToken ct) =>
        ApiResults.GuardAsync(_logger, async () =>
        {
            var (body, error) = await RequestReader.ReadJsonAsync<UpdateMenuItemRequest>(req, ct);
            if (error is not null) return ApiResults.BadRequest(error);

            var errors = ModelValidator.Validate(body!);
            if (errors.Count > 0) return ApiResults.ValidationFailed(errors);

            var sku = id.Trim().ToUpperInvariant();
            var entity = await _menu.GetAsync(category.Trim(), sku, ct);
            if (entity is null) return ApiResults.NotFound($"Menu item '{sku}' was not found in category '{category}'.");

            body!.ApplyTo(entity);
            await _menu.UpdateAsync(entity, ct);

            _logger.LogInformation("Updated menu item {Sku} in {Category}", sku, category);
            return new OkObjectResult(MenuItemResponse.From(entity));
        });

    /// <summary>DELETE /api/menu/{category}/{id} - removes an item from the menu.</summary>
    [Function("DeleteMenuItem")]
    public Task<IActionResult> DeleteMenuItem(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "menu/{category}/{id}")] HttpRequest req,
        string category,
        string id,
        CancellationToken ct) =>
        ApiResults.GuardAsync(_logger, async () =>
        {
            var sku = id.Trim().ToUpperInvariant();
            if (!await _menu.DeleteAsync(category.Trim(), sku, ct))
                return ApiResults.NotFound($"Menu item '{sku}' was not found in category '{category}'.");

            _logger.LogInformation("Deleted menu item {Sku} from {Category}", sku, category);
            return new NoContentResult();
        });
}
