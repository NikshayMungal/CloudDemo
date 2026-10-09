using System.Text.RegularExpressions;
using CoffeeNChill.Functions.Helpers;
using CoffeeNChill.Functions.Models;
using CoffeeNChill.Functions.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CoffeeNChill.Functions.Functions;

/// <summary>
/// HTTP functions for staff documents in the "staff-docs" share (barista recipe sheets, cleaning manuals, H&amp;S policies).
/// Files are streamed to and from storage - they are never fully buffered in memory by our code.
/// </summary>
public class DocumentFunctions
{
    // Safe file names only: no path separators, no leading dots, max 100 chars.
    private static readonly Regex SafeName = new(@"^[A-Za-z0-9][A-Za-z0-9 _.()\-]{0,99}$", RegexOptions.Compiled);

    private readonly IDocumentStore _store;
    private readonly ILogger<DocumentFunctions> _logger;

    public DocumentFunctions(IDocumentStore store, ILogger<DocumentFunctions> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/documents/upload (multipart/form-data, field "file").
    /// Optional query string: ?overwrite=true to replace an existing file.
    /// </summary>
    [Function("UploadStaffDocument")]
    public Task<IActionResult> UploadStaffDocument(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "documents/upload")] HttpRequest req,
        CancellationToken ct) =>
        ApiResults.GuardAsync(_logger, async () =>
        {
            if (!req.HasFormContentType)
                return ApiResults.UnsupportedMediaType("Send the file as multipart/form-data with a form field named 'file'.");

            var form = await req.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return ApiResults.BadRequest("No file was uploaded (or the file is empty). Use a form-data field named 'file'.");

            if (file.Length > DocumentTypes.MaxFileSizeBytes)
                return ApiResults.PayloadTooLarge($"Files may be at most {DocumentTypes.MaxFileSizeBytes / (1024 * 1024)} MB.");

            // Path.GetFileName strips any directory part a client might send (path traversal defence).
            var fileName = Path.GetFileName(file.FileName ?? string.Empty);
            if (!SafeName.IsMatch(fileName))
                return ApiResults.BadRequest("Invalid file name. Use letters, digits, spaces, '.', '_', '-', '(' or ')' (max 100 characters).");

            if (!DocumentTypes.IsAllowed(fileName, file.ContentType, out var contentType))
                return ApiResults.UnsupportedMediaType(
                    $"File type not allowed (or MIME type '{file.ContentType}' does not match the extension). Allowed extensions: {DocumentTypes.AllowedExtensionsText}.");

            var overwrite = string.Equals(req.Query["overwrite"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
            if (!overwrite && await _store.ExistsAsync(fileName, ct))
                return ApiResults.Conflict($"'{fileName}' already exists. Add ?overwrite=true to replace it.");

            await using var stream = file.OpenReadStream();
            await _store.UploadAsync(fileName, stream, file.Length, contentType, ct);

            _logger.LogInformation("Uploaded staff document {File} ({Bytes} bytes, {Type}) to {Backend}",
                fileName, file.Length, contentType, _store.BackendName);

            return new ObjectResult(new DocumentUploadResponse
            {
                FileName = fileName,
                SizeBytes = file.Length,
                ContentType = contentType,
                StorageBackend = _store.BackendName,
                DownloadUrl = $"/api/documents/download/{Uri.EscapeDataString(fileName)}"
            })
            { StatusCode = StatusCodes.Status201Created };
        });

    /// <summary>GET /api/documents - lists file name, size and last-modified date for every stored file.</summary>
    [Function("ListStaffDocuments")]
    public Task<IActionResult> ListStaffDocuments(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "documents")] HttpRequest req,
        CancellationToken ct) =>
        ApiResults.GuardAsync(_logger, async () =>
        {
            var docs = await _store.ListAsync(ct);
            return new OkObjectResult(new DocumentListResponse
            {
                StorageBackend = _store.BackendName,
                Count = docs.Count,
                Documents = docs
            });
        });

    /// <summary>GET /api/documents/download/{fileName} - streams the file back to the client.</summary>
    [Function("DownloadStaffDocument")]
    public Task<IActionResult> DownloadStaffDocument(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "documents/download/{fileName}")] HttpRequest req,
        string fileName,
        CancellationToken ct) =>
        ApiResults.GuardAsync(_logger, async () =>
        {
            var safe = Path.GetFileName(fileName ?? string.Empty);
            if (!SafeName.IsMatch(safe)) return ApiResults.BadRequest("Invalid file name.");

            var download = await _store.OpenReadAsync(safe, ct);
            if (download is null) return ApiResults.NotFound($"Document '{safe}' was not found.");

            _logger.LogInformation("Streaming staff document {File}", safe);
            // FileStreamResult copies the storage stream straight to the HTTP response.
            return new FileStreamResult(download.Content, download.ContentType) { FileDownloadName = safe };
        });
}
