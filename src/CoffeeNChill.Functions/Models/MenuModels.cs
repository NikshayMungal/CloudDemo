using System.ComponentModel.DataAnnotations;
using Azure;
using Azure.Data.Tables;

namespace CoffeeNChill.Functions.Models;

/// <summary>
/// Storage entity for the <c>MenuItems</c> Azure Table.
/// PartitionKey = Category (e.g. "Hot Drinks"), RowKey = SKU (e.g. "COF-001").
/// </summary>
public class MenuItemEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;   // Category
    public string RowKey { get; set; } = string.Empty;         // SKU / item id
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public double Price { get; set; }
    public bool IsAvailable { get; set; } = true;
}

/// <summary>DTO accepted by <c>POST /api/menu</c>.</summary>
public class CreateMenuItemRequest
{
    // Table keys may not contain / \ # ? or control characters.
    [Required(ErrorMessage = "Category is required.")]
    [StringLength(50, ErrorMessage = "Category must be 50 characters or fewer.")]
    [RegularExpression(@"^[^/\\#?\p{C}]+$", ErrorMessage = "Category contains illegal characters (/ \\ # ? or control characters).")]
    public string Category { get; set; } = string.Empty;

    [Required(ErrorMessage = "Sku is required.")]
    [StringLength(30, ErrorMessage = "Sku must be 30 characters or fewer.")]
    [RegularExpression(@"^[A-Za-z0-9_-]+$", ErrorMessage = "Sku may only contain letters, digits, '-' and '_' (e.g. COF-001).")]
    public string Sku { get; set; } = string.Empty;

    [Required(ErrorMessage = "Name is required.")]
    [StringLength(100, ErrorMessage = "Name must be 100 characters or fewer.")]
    public string Name { get; set; } = string.Empty;

    [StringLength(500, ErrorMessage = "Description must be 500 characters or fewer.")]
    public string Description { get; set; } = string.Empty;

    [Range(0.01, 10000.0, ErrorMessage = "Price must be between 0.01 and 10000.")]
    public double Price { get; set; }

    public bool IsAvailable { get; set; } = true;

    /// <summary>Maps the validated request DTO to a storage entity.</summary>
    public MenuItemEntity ToEntity() => new()
    {
        PartitionKey = Category.Trim(),
        RowKey = Sku.Trim().ToUpperInvariant(),
        Name = Name.Trim(),
        Description = (Description ?? string.Empty).Trim(),
        Price = Math.Round(Price, 2),
        IsAvailable = IsAvailable
    };
}

/// <summary>DTO accepted by <c>PUT /api/menu/{category}/{id}</c>. At least one field is required.</summary>
public class UpdateMenuItemRequest : IValidatableObject
{
    [Range(0.01, 10000.0, ErrorMessage = "Price must be between 0.01 and 10000.")]
    public double? Price { get; set; }

    public bool? IsAvailable { get; set; }

    [StringLength(100, MinimumLength = 1, ErrorMessage = "Name must be between 1 and 100 characters.")]
    public string? Name { get; set; }

    [StringLength(500, ErrorMessage = "Description must be 500 characters or fewer.")]
    public string? Description { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Price is null && IsAvailable is null && Name is null && Description is null)
        {
            yield return new ValidationResult(
                "Provide at least one field to update: price, isAvailable, name or description.",
                new[] { nameof(Price) });
        }
    }

    /// <summary>Applies only the supplied fields to an existing entity.</summary>
    public void ApplyTo(MenuItemEntity entity)
    {
        if (Price.HasValue) entity.Price = Math.Round(Price.Value, 2);
        if (IsAvailable.HasValue) entity.IsAvailable = IsAvailable.Value;
        if (Name is not null) entity.Name = Name.Trim();
        if (Description is not null) entity.Description = Description.Trim();
    }
}

/// <summary>DTO returned to API clients (never expose the raw table entity).</summary>
public class MenuItemResponse
{
    public string Category { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public double Price { get; set; }
    public bool IsAvailable { get; set; }
    public DateTimeOffset? LastModified { get; set; }

    public static MenuItemResponse From(MenuItemEntity e) => new()
    {
        Category = e.PartitionKey,
        Sku = e.RowKey,
        Name = e.Name,
        Description = e.Description,
        Price = e.Price,
        IsAvailable = e.IsAvailable,
        LastModified = e.Timestamp
    };
}
