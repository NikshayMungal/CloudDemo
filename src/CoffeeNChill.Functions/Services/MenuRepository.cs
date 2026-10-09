using Azure;
using Azure.Data.Tables;
using CoffeeNChill.Functions.Models;

namespace CoffeeNChill.Functions.Services;

public interface IMenuRepository
{
    /// <summary>Inserts a new item. Returns false when the (category, sku) pair already exists.</summary>
    Task<bool> TryAddAsync(MenuItemEntity entity, CancellationToken ct);
    Task<IReadOnlyList<MenuItemEntity>> GetAllAsync(CancellationToken ct);
    Task<IReadOnlyList<MenuItemEntity>> GetByCategoryAsync(string category, CancellationToken ct);
    Task<MenuItemEntity?> GetAsync(string category, string sku, CancellationToken ct);
    /// <summary>Finds an item by SKU across all categories (used to validate orders).</summary>
    Task<MenuItemEntity?> FindBySkuAsync(string sku, CancellationToken ct);
    Task UpdateAsync(MenuItemEntity entity, CancellationToken ct);
    Task<bool> DeleteAsync(string category, string sku, CancellationToken ct);
}

/// <summary>Storage concerns for the <c>MenuItems</c> table (kept separate from the HTTP functions).</summary>
public sealed class MenuRepository : TableRepositoryBase, IMenuRepository
{
    public MenuRepository(TableServiceClient serviceClient) : base(serviceClient, AppSettings.MenuTableName) { }

    public async Task<bool> TryAddAsync(MenuItemEntity entity, CancellationToken ct)
    {
        var table = await GetTableAsync(ct);
        try
        {
            await table.AddEntityAsync(entity, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            return false; // EntityAlreadyExists
        }
    }

    public async Task<IReadOnlyList<MenuItemEntity>> GetAllAsync(CancellationToken ct)
    {
        var table = await GetTableAsync(ct);
        var items = new List<MenuItemEntity>();
        await foreach (var e in table.QueryAsync<MenuItemEntity>(cancellationToken: ct))
            items.Add(e);
        return items.OrderBy(i => i.PartitionKey).ThenBy(i => i.RowKey).ToList();
    }

    public async Task<IReadOnlyList<MenuItemEntity>> GetByCategoryAsync(string category, CancellationToken ct)
    {
        var table = await GetTableAsync(ct);
        var items = new List<MenuItemEntity>();
        // Filtering on PartitionKey is the cheapest possible Table Storage query.
        await foreach (var e in table.QueryAsync<MenuItemEntity>(x => x.PartitionKey == category, cancellationToken: ct))
            items.Add(e);
        return items.OrderBy(i => i.RowKey).ToList();
    }

    public async Task<MenuItemEntity?> GetAsync(string category, string sku, CancellationToken ct)
    {
        var table = await GetTableAsync(ct);
        var response = await table.GetEntityIfExistsAsync<MenuItemEntity>(category, sku, cancellationToken: ct);
        return response.HasValue ? response.Value : null;
    }

    public async Task<MenuItemEntity?> FindBySkuAsync(string sku, CancellationToken ct)
    {
        var table = await GetTableAsync(ct);
        await foreach (var e in table.QueryAsync<MenuItemEntity>(x => x.RowKey == sku, cancellationToken: ct))
            return e;
        return null;
    }

    public async Task UpdateAsync(MenuItemEntity entity, CancellationToken ct)
    {
        var table = await GetTableAsync(ct);
        // ETag gives optimistic concurrency: a 412 is mapped to HTTP 409 by ApiResults.GuardAsync.
        await table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, ct);
    }

    public async Task<bool> DeleteAsync(string category, string sku, CancellationToken ct)
    {
        var table = await GetTableAsync(ct);
        var existing = await table.GetEntityIfExistsAsync<MenuItemEntity>(category, sku, cancellationToken: ct);
        if (!existing.HasValue) return false;

        try
        {
            await table.DeleteEntityAsync(category, sku, ETag.All, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }
}
