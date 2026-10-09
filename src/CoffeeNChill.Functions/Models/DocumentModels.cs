namespace CoffeeNChill.Functions.Models;

/// <summary>Metadata for a file stored in the <c>staff-docs</c> share.</summary>
public record StoredDocument(string FileName, long SizeBytes, string ContentType, DateTimeOffset? LastModified);

/// <summary>A readable stream plus the MIME type used for the HTTP response.</summary>
public sealed record DocumentDownload(Stream Content, string ContentType);

/// <summary>Response body for <c>GET /api/documents</c>.</summary>
public class DocumentListResponse
{
    public string StorageBackend { get; set; } = string.Empty;
    public int Count { get; set; }
    public IReadOnlyList<StoredDocument> Documents { get; set; } = Array.Empty<StoredDocument>();
}

/// <summary>Response body for a successful upload.</summary>
public class DocumentUploadResponse
{
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public string StorageBackend { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
}
