using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using CoffeeNChill.Functions.Models;

namespace CoffeeNChill.Functions.Services;

/// <summary>Abstraction over where staff documents are kept.</summary>
public interface IDocumentStore
{
    string Mode { get; }
    Task SaveAsync(string fileName, Stream content, long length, string contentType, CancellationToken ct = default);
    Task<List<DocumentInfo>> ListAsync(CancellationToken ct = default);
    Task<DocumentDownload?> OpenReadAsync(string fileName, CancellationToken ct = default);
}

/// <summary>
/// Real Azure Files share called "staff-docs".
/// NOTE: Azurite does NOT emulate Azure Files, so this mode needs a real storage account
/// (set DocumentStorageMode=FileShare and FileShareConnectionString).
/// </summary>
public class FileShareDocumentStore : IDocumentStore
{
    private readonly ShareClient _share;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialised;

    public string Mode => "FileShare";

    public FileShareDocumentStore(string connectionString) =>
        _share = new ShareClient(connectionString, "staff-docs");

    private async Task EnsureAsync(CancellationToken ct)
    {
        if (_initialised) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (!_initialised)
            {
                await _share.CreateIfNotExistsAsync();
                _initialised = true;
            }
        }
        finally { _initLock.Release(); }
    }

    public async Task SaveAsync(string fileName, Stream content, long length, string contentType, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        var file = _share.GetRootDirectoryClient().GetFileClient(fileName);
        await file.CreateAsync(length, httpHeaders: new ShareFileHttpHeaders { ContentType = contentType }, cancellationToken: ct);
        if (length > 0) await file.UploadAsync(content, new ShareFileUploadOptions(), ct);   // streamed, not buffered
    }

    public async Task<List<DocumentInfo>> ListAsync(CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        var root = _share.GetRootDirectoryClient();
        var result = new List<DocumentInfo>();
        await foreach (ShareFileItem item in root.GetFilesAndDirectoriesAsync())
        {
            if (item.IsDirectory) continue;
            var props = await root.GetFileClient(item.Name).GetPropertiesAsync(cancellationToken: ct);
            result.Add(new DocumentInfo(item.Name, props.Value.ContentLength, props.Value.LastModified, props.Value.ContentType));
        }
        return result.OrderBy(d => d.FileName).ToList();
    }

    public async Task<DocumentDownload?> OpenReadAsync(string fileName, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        var file = _share.GetRootDirectoryClient().GetFileClient(fileName);
        if (!(await file.ExistsAsync(ct)).Value) return null;
        var download = await file.DownloadAsync();
        return new DocumentDownload(download.Value.Content, download.Value.Details.ContentType ?? "application/octet-stream");
    }
}

/// <summary>
/// Local-development fallback that works on Azurite: a blob container also called "staff-docs".
/// The HTTP contract is identical, so Postman tests are the same in both modes.
/// </summary>
public class BlobDocumentStore : IDocumentStore
{
    private readonly BlobContainerClient _container;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialised;

    public string Mode => "Blob";

    public BlobDocumentStore(string connectionString) =>
        _container = new BlobContainerClient(connectionString, "staff-docs");

    private async Task EnsureAsync(CancellationToken ct)
    {
        if (_initialised) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (!_initialised)
            {
                await _container.CreateIfNotExistsAsync();
                _initialised = true;
            }
        }
        finally { _initLock.Release(); }
    }

    public async Task SaveAsync(string fileName, Stream content, long length, string contentType, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        var blob = _container.GetBlobClient(fileName);
        await blob.UploadAsync(content, new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, ct);
    }

    public async Task<List<DocumentInfo>> ListAsync(CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        var result = new List<DocumentInfo>();
        await foreach (BlobItem blob in _container.GetBlobsAsync(cancellationToken: ct))
            result.Add(new DocumentInfo(blob.Name, blob.Properties.ContentLength ?? 0, blob.Properties.LastModified, blob.Properties.ContentType));
        return result.OrderBy(d => d.FileName).ToList();
    }

    public async Task<DocumentDownload?> OpenReadAsync(string fileName, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        var blob = _container.GetBlobClient(fileName);
        if (!(await blob.ExistsAsync(ct)).Value) return null;
        var props = await blob.GetPropertiesAsync(cancellationToken: ct);
        var stream = await blob.OpenReadAsync(new BlobOpenReadOptions(allowModifications: false), ct);
        return new DocumentDownload(stream, props.Value.ContentType ?? "application/octet-stream");
    }
}

/// <summary>Chooses the store from the DocumentStorageMode setting (default: Blob, because Azurite has no Azure Files).</summary>
public static class DocumentStoreFactory
{
    public static IDocumentStore Create(string defaultConnectionString)
    {
        var mode = Environment.GetEnvironmentVariable("DocumentStorageMode") ?? "Blob";
        var conn = Environment.GetEnvironmentVariable("FileShareConnectionString") ?? defaultConnectionString;
        return mode.Equals("FileShare", StringComparison.OrdinalIgnoreCase)
            ? new FileShareDocumentStore(conn)
            : new BlobDocumentStore(conn);
    }
}
