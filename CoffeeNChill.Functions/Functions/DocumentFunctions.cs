using System.Text.RegularExpressions;
using Azure;
using CoffeeNChill.Functions.Helpers;
using CoffeeNChill.Functions.Models;
using CoffeeNChill.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CoffeeNChill.Functions.Functions;

/// <summary>Staff documents (recipe sheets, cleaning manuals, H&amp;S policies) in the staff-docs store.</summary>
public class DocumentFunctions
{
    private const long MaxBytes = 10 * 1024 * 1024; // 10 MB

    // Extension -> the MIME type we expect. Anything else is rejected.
    private static readonly Dictionary<string, string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
    };

    private static readonly Regex SafeName = new(@"^[\w\-. ()]{1,100}$", RegexOptions.Compiled);

    private readonly IDocumentStore _store;
    private readonly ILogger<DocumentFunctions> _logger;

    public DocumentFunctions(IDocumentStore store, ILogger<DocumentFunctions> logger)
    {
        _store = store;
        _logger = logger;
    }

    private static bool IsSafeFileName(string name) =>
        SafeName.IsMatch(name) && !name.Contains("..") && Path.GetFileName(name) == name;

    // POST /api/documents/upload   (multipart/form-data, field name "file")
    [Function("UploadStaffDocument")]
    public async Task<IActionResult> Upload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "documents/upload")] HttpRequest req,
        CancellationToken ct)
    {
        if (!req.HasFormContentType)
            return ApiHelpers.Error(400, "Content-Type must be multipart/form-data.");

        var form = await req.ReadFormAsync(ct);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();

        if (file is null || file.Length == 0)
            return ApiHelpers.Error(400, "Attach a non-empty file in the form field named 'file'.");
        if (file.Length > MaxBytes)
            return ApiHelpers.Error(413, "File is too large. The limit is 10 MB.");

        var fileName = Path.GetFileName(file.FileName);
        if (!IsSafeFileName(fileName))
            return ApiHelpers.Error(400, "Invalid file name. Use letters, numbers, spaces, '-', '_', '.', '(' and ')'.");

        var ext = Path.GetExtension(fileName);
        if (!AllowedTypes.TryGetValue(ext, out var expectedType))
            return ApiHelpers.Error(415, $"File type '{ext}' is not allowed. Allowed: {string.Join(", ", AllowedTypes.Keys)}.");

        // MIME validation: the declared type must match the extension (octet-stream is tolerated).
        var declared = file.ContentType ?? "";
        if (!declared.Equals(expectedType, StringComparison.OrdinalIgnoreCase) &&
            !declared.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
            return ApiHelpers.Error(415, $"Content type '{declared}' does not match '{ext}' (expected '{expectedType}').");

        try
        {
            await using var stream = file.OpenReadStream();
            await _store.SaveAsync(fileName, stream, file.Length, expectedType, ct);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Failed to store document {File}", fileName);
            return ApiHelpers.Error(503, "Document storage is currently unavailable.");
        }

        _logger.LogInformation("Uploaded {File} ({Size} bytes) using {Mode}", fileName, file.Length, _store.Mode);
        return new ObjectResult(new { fileName, sizeBytes = file.Length, contentType = expectedType, storage = _store.Mode })
        { StatusCode = 201 };
    }

    // GET /api/documents
    [Function("ListStaffDocuments")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "documents")] HttpRequest req,
        CancellationToken ct)
    {
        try
        {
            return new OkObjectResult(await _store.ListAsync(ct));
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Failed to list documents");
            return ApiHelpers.Error(503, "Document storage is currently unavailable.");
        }
    }

    // GET /api/documents/download/{fileName}
    [Function("DownloadStaffDocument")]
    public async Task<IActionResult> Download(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "documents/download/{fileName}")] HttpRequest req,
        string fileName, CancellationToken ct)
    {
        if (!IsSafeFileName(fileName))
            return ApiHelpers.Error(400, "Invalid file name.");

        try
        {
            var doc = await _store.OpenReadAsync(fileName, ct);
            if (doc is null) return ApiHelpers.Error(404, $"Document '{fileName}' was not found.");

            // Streams straight back to the client.
            return new FileStreamResult(doc.Content, doc.ContentType) { FileDownloadName = fileName };
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Failed to download {File}", fileName);
            return ApiHelpers.Error(503, "Document storage is currently unavailable.");
        }
    }
}
