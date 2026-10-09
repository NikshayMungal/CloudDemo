using Azure;
using Azure.Data.Tables;
using CoffeeNChill.Functions.Models;

namespace CoffeeNChill.Functions.Services;

public enum StatusUpdateOutcome { Updated, NotFound, InvalidTransition }

public sealed record StatusUpdateResult(StatusUpdateOutcome Outcome, OrderEntity? Order);

public interface IOrderRepository
{
    /// <summary>Creates the order row. If it already exists (duplicate delivery) the existing row is returned.</summary>
    Task<(OrderEntity Order, bool Created)> AddIfNotExistsAsync(OrderEntity entity, CancellationToken ct);
    Task<OrderEntity?> FindAsync(string orderId, CancellationToken ct);
    Task<IReadOnlyList<OrderEntity>> GetAllAsync(string? status, CancellationToken ct);
    /// <summary>Moves an order forward in the lifecycle. Going backwards or staying put is rejected.</summary>
    Task<StatusUpdateResult> TryUpdateStatusAsync(string orderId, string newStatus, CancellationToken ct);
}

/// <summary>Storage concerns for the <c>Orders</c> table.</summary>
public sealed class OrderRepository : TableRepositoryBase, IOrderRepository
{
    public OrderRepository(TableServiceClient serviceClient) : base(serviceClient, AppSettings.OrdersTableName) { }

    public async Task<(OrderEntity Order, bool Created)> AddIfNotExistsAsync(OrderEntity entity, CancellationToken ct)
    {
        var table = await GetTableAsync(ct);
        try
        {
            await table.AddEntityAsync(entity, ct);
            return (entity, true);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            var existing = await table.GetEntityAsync<OrderEntity>(entity.PartitionKey, entity.RowKey, cancellationToken: ct);
            return (existing.Value, false);
        }
    }

    public async Task<OrderEntity?> FindAsync(string orderId, CancellationToken ct)
    {
        var table = await GetTableAsync(ct);
        await foreach (var e in table.QueryAsync<OrderEntity>(x => x.RowKey == orderId, cancellationToken: ct))
            return e;
        return null;
    }

    public async Task<IReadOnlyList<OrderEntity>> GetAllAsync(string? status, CancellationToken ct)
    {
        var table = await GetTableAsync(ct);
        var orders = new List<OrderEntity>();
        await foreach (var e in table.QueryAsync<OrderEntity>(cancellationToken: ct))
        {
            if (status is null || e.Status == status) orders.Add(e);
        }
        return orders.OrderByDescending(o => o.OrderTimestamp).ToList();
    }

    public async Task<StatusUpdateResult> TryUpdateStatusAsync(string orderId, string newStatus, CancellationToken ct)
    {
        var table = await GetTableAsync(ct);

        // Retry on ETag conflicts: the queue simulator and a manual PUT may race each other.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var order = await FindAsync(orderId, ct);
            if (order is null) return new StatusUpdateResult(StatusUpdateOutcome.NotFound, null);

            if (OrderStatus.IndexOf(newStatus) <= OrderStatus.IndexOf(order.Status))
                return new StatusUpdateResult(StatusUpdateOutcome.InvalidTransition, order);

            var now = DateTimeOffset.UtcNow;
            order.Status = newStatus;
            order.StatusUpdatedAt = now;
            order.StatusHistory = $"{order.StatusHistory};{newStatus}@{now:O}";

            try
            {
                await table.UpdateEntityAsync(order, order.ETag, TableUpdateMode.Replace, ct);
                return new StatusUpdateResult(StatusUpdateOutcome.Updated, order);
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                // Someone else changed the row between our read and write - loop and re-read.
            }
        }

        throw new RequestFailedException(409, "Order status could not be updated because of repeated concurrent changes.");
    }
}
