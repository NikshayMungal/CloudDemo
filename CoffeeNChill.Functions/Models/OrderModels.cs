using System.ComponentModel.DataAnnotations;
using Azure;
using Azure.Data.Tables;

namespace CoffeeNChill.Functions.Models;

/// <summary>The JSON message placed on order-processing-queue.</summary>
public class Order
{
    public string OrderId { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public List<string> SelectedItemSKUs { get; set; } = new();
    public decimal TotalPrice { get; set; }
    public DateTime OrderTimestamp { get; set; }
}

/// <summary>Body of POST /api/orders/queue.</summary>
public class OrderRequest
{
    [Required(ErrorMessage = "OrderId is required.")]
    [RegularExpression(@"^[A-Za-z0-9\-]{3,40}$",
        ErrorMessage = "OrderId must be 3-40 characters: letters, numbers and '-'.")]
    public string? OrderId { get; set; }

    [Required(ErrorMessage = "CustomerName is required.")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "CustomerName must be 2-100 characters.")]
    public string? CustomerName { get; set; }

    [Required(ErrorMessage = "SelectedItemSKUs is required.")]
    [MinLength(1, ErrorMessage = "SelectedItemSKUs must contain at least one SKU.")]
    [MaxLength(50, ErrorMessage = "An order cannot contain more than 50 items.")]
    public List<string>? SelectedItemSKUs { get; set; }

    [Range(0.01, 100000, ErrorMessage = "TotalPrice must be greater than 0.")]
    public decimal TotalPrice { get; set; }

    /// <summary>Optional. The server generates one when it is missing.</summary>
    public DateTime? OrderTimestamp { get; set; }
}

public static class OrderStatus
{
    public const string Received = "Received";
    public const string Preparing = "Preparing";
    public const string Ready = "Ready";
    public const string Collected = "Collected";

    /// <summary>The barista lifecycle, in order.</summary>
    public static readonly string[] Lifecycle = { Received, Preparing, Ready, Collected };
}

/// <summary>Row in the Orders table. PartitionKey = OrderDate (yyyy-MM-dd), RowKey = OrderId.</summary>
public class OrderEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "";
    public string RowKey { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string CustomerName { get; set; } = "";
    public string SelectedItemSKUs { get; set; } = "";   // comma separated
    public double TotalPrice { get; set; }
    public DateTimeOffset OrderTimestamp { get; set; }
    public string Status { get; set; } = OrderStatus.Received;
    public DateTimeOffset LastUpdated { get; set; }
}

public record OrderStatusDto(
    string OrderId, string OrderDate, string CustomerName, List<string> SelectedItemSKUs,
    double TotalPrice, DateTimeOffset OrderTimestamp, string Status, DateTimeOffset LastUpdated);
