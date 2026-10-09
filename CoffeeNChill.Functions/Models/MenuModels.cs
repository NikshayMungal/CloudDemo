using System.ComponentModel.DataAnnotations;
using Azure;
using Azure.Data.Tables;
using CoffeeNChill.Functions.Helpers;

namespace CoffeeNChill.Functions.Models;

/// <summary>Row in the MenuItems table. PartitionKey = Category, RowKey = SKU.</summary>
public class MenuItemEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "";   // Category e.g. "Hot Drinks"
    public string RowKey { get; set; } = "";         // SKU e.g. "COF-001"
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public double Price { get; set; }
    public bool IsAvailable { get; set; } = true;
}

/// <summary>Body of POST /api/menu.</summary>
public class CreateMenuItemRequest
{
    [Required(ErrorMessage = "Category is required.")]
    [RegularExpression(ApiHelpers.CategoryPattern,
        ErrorMessage = "Category may contain letters, numbers, spaces, '&' and '-' (max 50).")]
    public string? Category { get; set; }

    [Required(ErrorMessage = "Sku is required.")]
    [RegularExpression(ApiHelpers.SkuPattern,
        ErrorMessage = "Sku must be 3-20 characters: letters, numbers and '-' (e.g. COF-001).")]
    public string? Sku { get; set; }

    [Required(ErrorMessage = "Name is required.")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "Name must be 2-100 characters.")]
    public string? Name { get; set; }

    [StringLength(300, ErrorMessage = "Description cannot exceed 300 characters.")]
    public string? Description { get; set; }

    [Range(0.01, 10000, ErrorMessage = "Price must be between 0.01 and 10000.")]
    public double Price { get; set; }

    public bool IsAvailable { get; set; } = true;
}

/// <summary>Body of PUT /api/menu/{category}/{id}. Only the supplied fields are changed.</summary>
public class UpdateMenuItemRequest
{
    [StringLength(100, MinimumLength = 2, ErrorMessage = "Name must be 2-100 characters.")]
    public string? Name { get; set; }

    [StringLength(300, ErrorMessage = "Description cannot exceed 300 characters.")]
    public string? Description { get; set; }

    [Range(0.01, 10000, ErrorMessage = "Price must be between 0.01 and 10000.")]
    public double? Price { get; set; }

    public bool? IsAvailable { get; set; }
}

/// <summary>What the API returns for a menu item.</summary>
public record MenuItemDto(string Category, string Sku, string Name, string Description, double Price, bool IsAvailable);
