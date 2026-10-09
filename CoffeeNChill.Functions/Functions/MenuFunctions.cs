using Azure;
using CoffeeNChill.Functions.Helpers;
using CoffeeNChill.Functions.Models;
using CoffeeNChill.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CoffeeNChill.Functions.Functions;

/// <summary>Menu CRUD against the MenuItems table (PartitionKey = Category, RowKey = SKU).</summary>
public class MenuFunctions
{
    private readonly IMenuRepository _repo;
    private readonly ILogger<MenuFunctions> _logger;

    public MenuFunctions(IMenuRepository repo, ILogger<MenuFunctions> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    private static MenuItemDto ToDto(MenuItemEntity e) =>
        new(e.PartitionKey, e.RowKey, e.Name, e.Description, e.Price, e.IsAvailable);

    private IActionResult StorageUnavailable(RequestFailedException ex)
    {
        _logger.LogError(ex, "Table storage error");
        return ApiHelpers.Error(503, "Storage is currently unavailable. Please try again.");
    }

    // POST /api/menu
    [Function("CreateMenuItem")]
    public async Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "menu")] HttpRequest req)
    {
        var (body, error) = await ApiHelpers.ReadBodyAsync<CreateMenuItemRequest>(req);
        if (error is not null) return error;

        var entity = new MenuItemEntity
        {
            PartitionKey = body!.Category!.Trim(),
            RowKey = body.Sku!.Trim().ToUpperInvariant(),
            Name = body.Name!.Trim(),
            Description = body.Description?.Trim() ?? "",
            Price = body.Price,
            IsAvailable = body.IsAvailable
        };

        try
        {
            await _repo.AddAsync(entity);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            return ApiHelpers.Error(409, $"Menu item '{entity.RowKey}' already exists in '{entity.PartitionKey}'.");
        }
        catch (RequestFailedException ex)
        {
            return StorageUnavailable(ex);
        }

        _logger.LogInformation("Created menu item {Sku} in {Category}", entity.RowKey, entity.PartitionKey);
        return new ObjectResult(ToDto(entity)) { StatusCode = 201 };
    }

    // GET /api/menu
    [Function("GetAllMenuItems")]
    public async Task<IActionResult> GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "menu")] HttpRequest req)
    {
        try
        {
            var items = await _repo.GetAllAsync();
            return new OkObjectResult(items.Select(ToDto).OrderBy(i => i.Category).ThenBy(i => i.Sku));
        }
        catch (RequestFailedException ex)
        {
            return StorageUnavailable(ex);
        }
    }

    // GET /api/menu/category/{category}
    [Function("GetMenuItemsByCategory")]
    public async Task<IActionResult> GetByCategory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "menu/category/{category}")] HttpRequest req,
        string category)
    {
        if (!ApiHelpers.IsValidCategory(category))
            return ApiHelpers.Error(400, "Invalid category.");

        try
        {
            var items = await _repo.GetByCategoryAsync(category);
            return new OkObjectResult(items.Select(ToDto).OrderBy(i => i.Sku));
        }
        catch (RequestFailedException ex)
        {
            return StorageUnavailable(ex);
        }
    }

    // PUT /api/menu/{category}/{id}
    [Function("UpdateMenuItem")]
    public async Task<IActionResult> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "menu/{category}/{id}")] HttpRequest req,
        string category, string id)
    {
        if (!ApiHelpers.IsValidCategory(category) || !ApiHelpers.IsValidSku(id))
            return ApiHelpers.Error(400, "Invalid category or item id.");

        var (body, error) = await ApiHelpers.ReadBodyAsync<UpdateMenuItemRequest>(req);
        if (error is not null) return error;

        if (body!.Name is null && body.Description is null && body.Price is null && body.IsAvailable is null)
            return ApiHelpers.Error(400, "Provide at least one of: name, description, price, isAvailable.");

        try
        {
            var entity = await _repo.GetAsync(category, id.ToUpperInvariant());
            if (entity is null)
                return ApiHelpers.Error(404, $"Menu item '{id}' was not found in '{category}'.");

            if (body.Name is not null) entity.Name = body.Name.Trim();
            if (body.Description is not null) entity.Description = body.Description.Trim();
            if (body.Price is not null) entity.Price = body.Price.Value;
            if (body.IsAvailable is not null) entity.IsAvailable = body.IsAvailable.Value;

            await _repo.UpdateAsync(entity);
            return new OkObjectResult(ToDto(entity));
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            return ApiHelpers.Error(409, "The item was changed by someone else. Reload and try again.");
        }
        catch (RequestFailedException ex)
        {
            return StorageUnavailable(ex);
        }
    }

    // DELETE /api/menu/{category}/{id}
    [Function("DeleteMenuItem")]
    public async Task<IActionResult> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "menu/{category}/{id}")] HttpRequest req,
        string category, string id)
    {
        if (!ApiHelpers.IsValidCategory(category) || !ApiHelpers.IsValidSku(id))
            return ApiHelpers.Error(400, "Invalid category or item id.");

        try
        {
            var deleted = await _repo.DeleteAsync(category, id.ToUpperInvariant());
            return deleted
                ? new NoContentResult()
                : ApiHelpers.Error(404, $"Menu item '{id}' was not found in '{category}'.");
        }
        catch (RequestFailedException ex)
        {
            return StorageUnavailable(ex);
        }
    }
}
