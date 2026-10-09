using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace CoffeeNChill.Functions.Helpers;

/// <summary>Safe JSON body reading for HTTP triggers.</summary>
public static class RequestReader
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Deserializes the request body. Returns an error message instead of throwing for bad JSON.</summary>
    public static async Task<(T? Value, string? Error)> ReadJsonAsync<T>(HttpRequest req, CancellationToken ct) where T : class
    {
        try
        {
            var value = await JsonSerializer.DeserializeAsync<T>(req.Body, JsonOptions, ct);
            return value is null ? (null, "Request body is empty.") : (value, null);
        }
        catch (JsonException)
        {
            return (null, "Request body is not valid JSON (or has fields of the wrong type).");
        }
    }
}

/// <summary>Runs DataAnnotations validation and returns a camelCase field -> messages map.</summary>
public static class ModelValidator
{
    public static Dictionary<string, string[]> Validate(object model)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);

        return results
            .SelectMany(r =>
            {
                var members = r.MemberNames.Any() ? r.MemberNames : new[] { "model" };
                return members.Select(m => (Member: ToCamel(m), Message: r.ErrorMessage ?? "Invalid value."));
            })
            .GroupBy(x => x.Member)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Message).Distinct().ToArray());
    }

    private static string ToCamel(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
