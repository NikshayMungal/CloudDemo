namespace CoffeeNChill.Functions.Models;

/// <summary>Metadata returned by GET /api/documents.</summary>
public record DocumentInfo(string FileName, long SizeBytes, DateTimeOffset? LastModified, string? ContentType);

/// <summary>A readable stream plus its MIME type, used by the download endpoint.</summary>
public record DocumentDownload(Stream Content, string ContentType);
