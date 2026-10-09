using Azure;
using Azure.Data.Tables;
using CoffeeNChill.Functions.Models;

namespace CoffeeNChill.Functions.Services;

/// <summary>Storage concerns for the Orders table.</summary>
public interface IOrderRepository
{
    Task CreateAsync(Order order);
    Task UpdateStatusAsync(string orderDate, string orderId, string status);
    Task<OrderStatusDto?> GetAsync(string orderDate, string orderId);
    Task<List<OrderStatusDto>> ListAsync(string? orderDate);
}

public class OrderRepository : IOrderRepository
{
    private readonly TableClient _table;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialised;

    public OrderRepository(TableServiceClient service) => _table = service.GetTableClient("Orders");

    public static string DateKey(DateTime timestamp) => timestamp.ToUniversalTime().ToString("yyyy-MM-dd");

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

    /// <summary>Inserts the order with Status = "Received" (upsert so queue retries are safe).</summary>
    public async Task CreateAsync(Order order)
    {
        await EnsureTableAsync();
        var entity = new OrderEntity
        {
            PartitionKey = DateKey(order.OrderTimestamp),
            RowKey = order.OrderId,
            CustomerName = order.CustomerName,
            SelectedItemSKUs = string.Join(",", order.SelectedItemSKUs),
            TotalPrice = (double)order.TotalPrice,
            OrderTimestamp = new DateTimeOffset(order.OrderTimestamp.ToUniversalTime()),
            Status = OrderStatus.Received,
            LastUpdated = DateTimeOffset.UtcNow
        };
        await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace);
    }

    /// <summary>Merges only the status fields so the other columns are untouched.</summary>
    public async Task UpdateStatusAsync(string orderDate, string orderId, string status)
    {
        await EnsureTableAsync();
        var patch = new TableEntity(orderDate, orderId)
        {
            ["Status"] = status,
            ["LastUpdated"] = DateTimeOffset.UtcNow
        };
        await _table.UpdateEntityAsync(patch, ETag.All, TableUpdateMode.Merge);
    }

    public async Task<OrderStatusDto?> GetAsync(string orderDate, string orderId)
    {
        await EnsureTableAsync();
        var result = await _table.GetEntityIfExistsAsync<OrderEntity>(orderDate, orderId);
        return result.HasValue ? ToDto(result.Value!) : null;
    }

    public async Task<List<OrderStatusDto>> ListAsync(string? orderDate)
    {
        await EnsureTableAsync();
        var list = new List<OrderStatusDto>();
        var query = orderDate is null
            ? _table.QueryAsync<OrderEntity>()
            : _table.QueryAsync<OrderEntity>(x => x.PartitionKey == orderDate);
        await foreach (var e in query) list.Add(ToDto(e));
        return list.OrderBy(o => o.OrderTimestamp).ToList();
    }

    private static OrderStatusDto ToDto(OrderEntity e) => new(
        e.RowKey, e.PartitionKey, e.CustomerName,
        e.SelectedItemSKUs.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
        e.TotalPrice, e.OrderTimestamp, e.Status, e.LastUpdated);
}
