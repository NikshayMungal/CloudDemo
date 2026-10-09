using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoffeeNChill.Functions.Helpers;

/// <summary>Shared request parsing, validation and error-response helpers.</summary>
public static class ApiHelpers
{
    // Table keys may not contain / \ # ? so category and SKU are restricted.
    public const string CategoryPattern = @"^[A-Za-z0-9][A-Za-z0-9 &\-]{0,49}$";
    public const string SkuPattern = @"^[A-Za-z0-9\-]{3,20}$";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool IsValidCategory(string? v) => v is not null && Regex.IsMatch(v, CategoryPattern);
    public static bool IsValidSku(string? v) => v is not null && Regex.IsMatch(v, SkuPattern);

    /// <summary>Consistent error body: { "message": "...", "errors": [...] }.</summary>
    public static IActionResult Error(int status, string message, IEnumerable<string>? errors = null) =>
        new ObjectResult(new { message, errors = errors ?? Array.Empty<string>() }) { StatusCode = status };

    /// <summary>Deserialises the JSON body and runs DataAnnotation validation.</summary>
    public static async Task<(T? Value, IActionResult? Error)> ReadBodyAsync<T>(HttpRequest req) where T : class
    {
        try
        {
            var value = await JsonSerializer.DeserializeAsync<T>(req.Body, JsonOptions);
            if (value is null) return (null, Error(400, "Request body is required."));

            var errors = Validate(value);
            if (errors.Count > 0) return (null, Error(400, "Validation failed.", errors));

            return (value, null);
        }
        catch (JsonException)
        {
            return (null, Error(400, "Request body is not valid JSON or has a wrongly typed field."));
        }
    }

    public static List<string> Validate(object instance)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);
        return results.Select(r => r.ErrorMessage ?? "Invalid value.").ToList();
    }
}
