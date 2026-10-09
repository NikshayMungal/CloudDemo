using Azure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace CoffeeNChill.Functions.Helpers;

/// <summary>Consistent JSON error body returned by every endpoint.</summary>
public class ErrorResponse
{
    public int Status { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public IDictionary<string, string[]>? Errors { get; set; }
}

/// <summary>
/// Factory methods for HTTP error responses plus <see cref="GuardAsync"/>, which acts as
/// a central exception handler so no storage failure ever leaks as an unhandled 500 stack trace.
/// </summary>
public static class ApiResults
{
    public static IActionResult Error(int status, string title, string detail, IDictionary<string, string[]>? errors = null) =>
        new ObjectResult(new ErrorResponse { Status = status, Title = title, Detail = detail, Errors = errors })
        {
            StatusCode = status
        };

    public static IActionResult BadRequest(string detail) => Error(400, "Bad Request", detail);
    public static IActionResult ValidationFailed(IDictionary<string, string[]> errors) =>
        Error(400, "Validation failed", "One or more fields are invalid.", errors);
    public static IActionResult NotFound(string detail) => Error(404, "Not Found", detail);
    public static IActionResult Conflict(string detail) => Error(409, "Conflict", detail);
    public static IActionResult UnsupportedMediaType(string detail) => Error(415, "Unsupported Media Type", detail);
    public static IActionResult PayloadTooLarge(string detail) => Error(413, "Payload Too Large", detail);

    /// <summary>Runs an endpoint body and converts storage/unexpected exceptions into clean HTTP responses.</summary>
    public static async Task<IActionResult> GuardAsync(ILogger logger, Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            logger.LogWarning(ex, "Storage resource not found ({Code})", ex.ErrorCode);
            return NotFound("The requested resource does not exist.");
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            logger.LogWarning(ex, "Storage conflict ({Code})", ex.ErrorCode);
            return Conflict("The resource was modified or already exists. Please retry.");
        }
        catch (RequestFailedException ex)
        {
            // Status 0 = could not reach storage (e.g. Azurite container not running).
            logger.LogError(ex, "Azure Storage request failed ({Status} {Code})", ex.Status, ex.ErrorCode);
            return Error(503, "Storage unavailable",
                "The storage service could not be reached. Check that Azurite is running and the connection string is correct.");
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Request cancelled by the client.");
            return new StatusCodeResult(499);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error while processing request");
            return Error(500, "Internal Server Error", "An unexpected error occurred. See the function logs for details.");
        }
    }
}
