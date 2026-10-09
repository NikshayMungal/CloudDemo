using Azure;
using Azure.Data.Tables;
using CoffeeNChill.Functions.Models;

namespace CoffeeNChill.Functions.Services;

/// <summary>Storage concerns for the MenuItems table, kept out of the HTTP functions.</summary>
public interface IMenuRepository
{
    Task AddAsync(MenuItemEntity entity);
    Task<List<MenuItemEntity>> GetAllAsync();
    Task<List<MenuItemEntity>> GetByCategoryAsync(string category);
    Task<MenuItemEntity?> GetAsync(string category, string sku);
    Task UpdateAsync(MenuItemEntity entity);
    Task<bool> DeleteAsync(string category, string sku);
}

public class MenuRepository : IMenuRepository
{
    private readonly TableClient _table;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialised;

    public MenuRepository(TableServiceClient service) => _table = service.GetTableClient("MenuItems");

    private async Task EnsureTableAsync()
    {
        if (_initialised) return;
        await _initLock.WaitAsync();
        try
        {
            if (!_initialised)
            {
                await _table.CreateIfNotExistsAsync();
                _initialised = true;
            }
        }
        finally { _initLock.Release(); }
    }

    /// <summary>Throws RequestFailedException (409) if the SKU already exists in that category.</summary>
    public async Task AddAsync(MenuItemEntity entity)
    {
        await EnsureTableAsync();
        await _table.AddEntityAsync(entity);
    }

    public async Task<List<MenuItemEntity>> GetAllAsync()
    {
        await EnsureTableAsync();
        var items = new List<MenuItemEntity>();
        await foreach (var e in _table.QueryAsync<MenuItemEntity>()) items.Add(e);
        return items;
    }

    public async Task<List<MenuItemEntity>> GetByCategoryAsync(string category)
    {
        await EnsureTableAsync();
        var items = new List<MenuItemEntity>();
        await foreach (var e in _table.QueryAsync<MenuItemEntity>(x => x.PartitionKey == category)) items.Add(e);
        return items;
    }

    public async Task<MenuItemEntity?> GetAsync(string category, string sku)
    {
        await EnsureTableAsync();
        var result = await _table.GetEntityIfExistsAsync<MenuItemEntity>(category, sku);
        return result.HasValue ? result.Value : null;
    }

    /// <summary>Uses the ETag so a concurrent edit raises 412 instead of being overwritten.</summary>
    public async Task UpdateAsync(MenuItemEntity entity)
    {
        await EnsureTableAsync();
        await _table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
    }

    public async Task<bool> DeleteAsync(string category, string sku)
    {
        await EnsureTableAsync();
        try
        {
            await _table.DeleteEntityAsync(category, sku);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }
}
