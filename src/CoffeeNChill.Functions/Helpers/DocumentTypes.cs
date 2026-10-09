namespace CoffeeNChill.Functions.Helpers;

/// <summary>
/// Whitelist of document types staff may upload (extension -> accepted MIME types).
/// The first MIME type in each list is the canonical one stored with the file.
/// </summary>
public static class DocumentTypes
{
    public const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB
    private const string Octet = "application/octet-stream";

    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = new[] { "application/pdf" },
        [".txt"] = new[] { "text/plain" },
        [".md"] = new[] { "text/markdown", "text/x-markdown", "text/plain" },
        [".csv"] = new[] { "text/csv", "application/vnd.ms-excel", "text/plain" },
        [".png"] = new[] { "image/png" },
        [".jpg"] = new[] { "image/jpeg" },
        [".jpeg"] = new[] { "image/jpeg" },
        [".docx"] = new[] { "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
        [".xlsx"] = new[] { "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" }
    };

    public static string AllowedExtensionsText => string.Join(", ", Allowed.Keys);

    /// <summary>True when both the extension and the declared MIME type are acceptable.</summary>
    public static bool IsAllowed(string fileName, string? declaredContentType, out string canonicalContentType)
    {
        canonicalContentType = Octet;
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext) || !Allowed.TryGetValue(ext, out var accepted))
            return false;

        // Strip parameters such as "; charset=utf-8"
        var declared = (declaredContentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        if (declared.Length > 0 && declared != Octet && !accepted.Contains(declared))
            return false;

        canonicalContentType = accepted[0];
        return true;
    }

    /// <summary>MIME type for a stored file name (falls back to application/octet-stream).</summary>
    public static string ContentTypeFor(string fileName) =>
        Allowed.TryGetValue(Path.GetExtension(fileName), out var accepted) ? accepted[0] : Octet;
}
