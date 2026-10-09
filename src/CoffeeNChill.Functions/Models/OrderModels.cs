using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Azure;
using Azure.Data.Tables;

namespace CoffeeNChill.Functions.Models;

/// <summary>The four barista lifecycle states, in order.</summary>
public static class OrderStatus
{
    public const string Received = "Received";
    public const string Preparing = "Preparing";
    public const string Ready = "Ready";
    public const string Collected = "Collected";

    public static readonly string[] Lifecycle = { Received, Preparing, Ready, Collected };

    /// <summary>Returns the canonical spelling of a status, or null if it is not a valid status.</summary>
    public static string? Normalize(string? value) =>
        Lifecycle.FirstOrDefault(s => string.Equals(s, value?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static int IndexOf(string status) => Array.IndexOf(Lifecycle, status);
}

/// <summary>
/// The JSON message placed on <c>order-processing-queue</c>.
/// Property names are PascalCase to match the brief exactly.
/// </summary>
public class OrderMessage
{
    [Required] public string OrderId { get; set; } = string.Empty;
    [Required] public string CustomerName { get; set; } = string.Empty;
    [Required, MinLength(1, ErrorMessage = "At least one item SKU is required.")]
    public List<string> SelectedItemSKUs { get; set; } = new();
    [Range(0.01, 100000.0, ErrorMessage = "TotalPrice must be greater than zero.")]
    public double TotalPrice { get; set; }
    public DateTimeOffset OrderTimestamp { get; set; }
}

/// <summary>DTO accepted by <c>POST /api/orders/queue</c>. OrderId and OrderTimestamp are optional (server generates them).</summary>
public class QueueOrderRequest : IValidatableObject
{
    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9_-]{3,40}$", RegexOptions.Compiled);
    private static readonly Regex SkuPattern = new(@"^[A-Za-z0-9_-]{1,30}$", RegexOptions.Compiled);

    public string? OrderId { get; set; }

    [Required(ErrorMessage = "CustomerName is required.")]
    [StringLength(100, ErrorMessage = "CustomerName must be 100 characters or fewer.")]
    public string CustomerName { get; set; } = string.Empty;

    [Required(ErrorMessage = "SelectedItemSKUs is required.")]
    [MinLength(1, ErrorMessage = "Select at least one item.")]
    [MaxLength(50, ErrorMessage = "An order may contain at most 50 items.")]
    public List<string> SelectedItemSKUs { get; set; } = new();

    [Range(0.01, 100000.0, ErrorMessage = "TotalPrice must be between 0.01 and 100000.")]
    public double TotalPrice { get; set; }

    public DateTimeOffset? OrderTimestamp { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrWhiteSpace(OrderId) && !IdPattern.IsMatch(OrderId.Trim()))
            yield return new ValidationResult(
                "OrderId must be 3-40 characters: letters, digits, '-' or '_' (e.g. ORD-2026-8801).",
                new[] { nameof(OrderId) });

        if (SelectedItemSKUs is not null && SelectedItemSKUs.Any(s => s is null || !SkuPattern.IsMatch(s.Trim())))
            yield return new ValidationResult(
                "Every SKU must contain only letters, digits, '-' or '_'.",
                new[] { nameof(SelectedItemSKUs) });
    }

    /// <summary>Builds the queue message, generating the id/timestamp when the client omitted them.</summary>
    public OrderMessage ToMessage()
    {
        var now = DateTimeOffset.UtcNow;
        return new OrderMessage
        {
            OrderId = string.IsNullOrWhiteSpace(OrderId)
                ? $"ORD-{now:yyyy}-{Random.Shared.Next(100000, 999999)}"
                : OrderId.Trim().ToUpperInvariant(),
            CustomerName = CustomerName.Trim(),
            SelectedItemSKUs = SelectedItemSKUs.Select(s => s.Trim().ToUpperInvariant()).ToList(),
            TotalPrice = Math.Round(TotalPrice, 2),
            OrderTimestamp = OrderTimestamp ?? now
        };
    }
}

/// <summary>Request body for <c>PUT /api/orders/{orderId}/status</c>.</summary>
public class UpdateOrderStatusRequest
{
    [Required(ErrorMessage = "Status is required.")]
    public string Status { get; set; } = string.Empty;
}

/// <summary>Storage entity for the <c>Orders</c> Azure Table (PartitionKey = order date, RowKey = order id).</summary>
public class OrderEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;   // OrderDate (yyyy-MM-dd, UTC)
    public string RowKey { get; set; } = string.Empty;         // OrderId
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string CustomerName { get; set; } = string.Empty;
    public string SelectedItemSKUs { get; set; } = string.Empty;   // comma separated
    public double TotalPrice { get; set; }
    public DateTimeOffset OrderTimestamp { get; set; }
    public string Status { get; set; } = OrderStatus.Received;
    public DateTimeOffset StatusUpdatedAt { get; set; }
    /// <summary>Audit trail: "Received@2026-...;Preparing@2026-..." (status@ISO-8601 UTC).</summary>
    public string StatusHistory { get; set; } = string.Empty;

    public static OrderEntity From(OrderMessage m)
    {
        var now = DateTimeOffset.UtcNow;
        return new OrderEntity
        {
            PartitionKey = m.OrderTimestamp.UtcDateTime.ToString("yyyy-MM-dd"),
            RowKey = m.OrderId,
            CustomerName = m.CustomerName,
            SelectedItemSKUs = string.Join(",", m.SelectedItemSKUs),
            TotalPrice = m.TotalPrice,
            OrderTimestamp = m.OrderTimestamp,
            Status = OrderStatus.Received,
            StatusUpdatedAt = now,
            StatusHistory = $"{OrderStatus.Received}@{now:O}"
        };
    }
}

public class StatusHistoryEntry
{
    public string Status { get; set; } = string.Empty;
    public string At { get; set; } = string.Empty;
}

/// <summary>DTO returned to API clients for an order.</summary>
public class OrderResponse
{
    public string OrderId { get; set; } = string.Empty;
    public string OrderDate { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public List<string> SelectedItemSKUs { get; set; } = new();
    public double TotalPrice { get; set; }
    public DateTimeOffset OrderTimestamp { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset StatusUpdatedAt { get; set; }
    public List<StatusHistoryEntry> StatusHistory { get; set; } = new();

    public static OrderResponse From(OrderEntity e) => new()
    {
        OrderId = e.RowKey,
        OrderDate = e.PartitionKey,
        CustomerName = e.CustomerName,
        SelectedItemSKUs = e.SelectedItemSKUs.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
        TotalPrice = e.TotalPrice,
        OrderTimestamp = e.OrderTimestamp,
        Status = e.Status,
        StatusUpdatedAt = e.StatusUpdatedAt,
        StatusHistory = e.StatusHistory
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('@', 2))
            .Select(p => new StatusHistoryEntry { Status = p[0], At = p.Length > 1 ? p[1] : string.Empty })
            .ToList()
    };
}

/// <summary>Response returned when an order is accepted onto the queue.</summary>
public class QueuedOrderResponse
{
    public string OrderId { get; set; } = string.Empty;
    public string Status { get; set; } = "Queued";
    public string Queue { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public DateTimeOffset OrderTimestamp { get; set; }
    public string StatusUrl { get; set; } = string.Empty;
}

/// <summary>Envelope written to the poison queue so failures can be diagnosed.</summary>
public class PoisonEnvelope
{
    public string FailureReason { get; set; } = string.Empty;
    public DateTimeOffset FailedAt { get; set; } = DateTimeOffset.UtcNow;
    public string OriginalMessage { get; set; } = string.Empty;
}
