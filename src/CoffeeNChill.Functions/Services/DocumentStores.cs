using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using CoffeeNChill.Functions.Helpers;
using CoffeeNChill.Functions.Models;

namespace CoffeeNChill.Functions.Services;

/// <summary>Abstraction over where staff documents live so the HTTP functions do not care.</summary>
public interface IDocumentStore
{
    /// <summary>Human readable name of the backing service (shown in API responses).</summary>
    string BackendName { get; }
    Task<bool> ExistsAsync(string fileName, CancellationToken ct);
    Task UploadAsync(string fileName, Stream content, long length, string contentType, CancellationToken ct);
    Task<IReadOnlyList<StoredDocument>> ListAsync(CancellationToken ct);
    Task<DocumentDownload?> OpenReadAsync(string fileName, CancellationToken ct);
}

/// <summary>
/// Real Azure Files implementation of the <c>staff-docs</c> share.
/// Used when the <c>FileShareConnectionString</c> setting is supplied (Azurite does NOT emulate Azure Files).
/// </summary>
public sealed class FileShareDocumentStore : IDocumentStore
{
    private readonly ShareClient _share;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _ready;

    public FileShareDocumentStore(string connectionString)
    {
        _share = new ShareClient(connectionString, AppSettings.ShareName);
    }

    public string BackendName => "AzureFileShare";

    private async Task<ShareDirectoryClient> RootAsync(CancellationToken ct)
    {
        if (!_ready)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (!_ready)
                {
                    await _share.CreateIfNotExistsAsync(cancellationToken: ct);
                    _ready = true;
                }
            }
            finally { _gate.Release(); }
        }
        return _share.GetRootDirectoryClient();
    }

    public async Task<bool> ExistsAsync(string fileName, CancellationToken ct)
    {
        var root = await RootAsync(ct);
        var exists = await root.GetFileClient(fileName).ExistsAsync(ct);
        return exists.Value;
    }

    public async Task UploadAsync(string fileName, Stream content, long length, string contentType, CancellationToken ct)
    {
        var root = await RootAsync(ct);
        var file = root.GetFileClient(fileName);

        // Azure Files needs the final size up-front, then the stream is sent in ranges (no full buffering in memory).
        await file.CreateAsync(length, new ShareFileCreateOptions
        {
            HttpHeaders = new ShareFileHttpHeaders { ContentType = contentType }
        }, cancellationToken: ct);
        if (length > 0)
            await file.UploadAsync(content, new ShareFileUploadOptions(), ct);
    }

    public async Task<IReadOnlyList<StoredDocument>> ListAsync(CancellationToken ct)
    {
        var root = await RootAsync(ct);
        var docs = new List<StoredDocument>();
        await foreach (var item in root.GetFilesAndDirectoriesAsync(cancellationToken: ct))
        {
            if (item.IsDirectory) continue;
            var props = (await root.GetFileClient(item.Name).GetPropertiesAsync(cancellationToken: ct)).Value;
            docs.Add(new StoredDocument(item.Name, props.ContentLength, DocumentTypes.ContentTypeFor(item.Name), props.LastModified));
        }
        return docs.OrderBy(d => d.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<DocumentDownload?> OpenReadAsync(string fileName, CancellationToken ct)
    {
        var root = await RootAsync(ct);
        var file = root.GetFileClient(fileName);
        if (!(await file.ExistsAsync(ct)).Value) return null;

        var download = await file.DownloadAsync(new ShareFileDownloadOptions(), ct);
        return new DocumentDownload(download.Value.Content, DocumentTypes.ContentTypeFor(fileName));
    }
}

/// <summary>
/// Local-development implementation: stores staff documents in a Blob container named <c>staff-docs</c>
/// on Azurite, because Azurite emulates Blob/Queue/Table but not Azure Files.
/// Behaviour (names, metadata, streaming) is identical from the API consumer's point of view.
/// </summary>
public sealed class BlobDocumentStore : IDocumentStore
{
    private readonly BlobContainerClient _container;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _ready;

    public BlobDocumentStore(string connectionString)
    {
        _container = new BlobContainerClient(connectionString, AppSettings.ShareName);
    }

    public string BackendName => "AzuriteBlobContainer (emulates the staff-docs file share)";

    private async Task<BlobContainerClient> ContainerAsync(CancellationToken ct)
    {
        if (!_ready)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (!_ready)
                {
                    await _container.CreateIfNotExistsAsync(cancellationToken: ct);
                    _ready = true;
                }
            }
            finally { _gate.Release(); }
        }
        return _container;
    }

    public async Task<bool> ExistsAsync(string fileName, CancellationToken ct)
    {
        var c = await ContainerAsync(ct);
        return (await c.GetBlobClient(fileName).ExistsAsync(ct)).Value;
    }

    public async Task UploadAsync(string fileName, Stream content, long length, string contentType, CancellationToken ct)
    {
        var c = await ContainerAsync(ct);
        var options = new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } };
        await c.GetBlobClient(fileName).UploadAsync(content, options, ct);
    }

    public async Task<IReadOnlyList<StoredDocument>> ListAsync(CancellationToken ct)
    {
        var c = await ContainerAsync(ct);
        var docs = new List<StoredDocument>();
        await foreach (var blob in c.GetBlobsAsync(cancellationToken: ct))
        {
            docs.Add(new StoredDocument(
                blob.Name,
                blob.Properties.ContentLength ?? 0,
                DocumentTypes.ContentTypeFor(blob.Name),
                blob.Properties.LastModified));
        }
        return docs.OrderBy(d => d.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<DocumentDownload?> OpenReadAsync(string fileName, CancellationToken ct)
    {
        var c = await ContainerAsync(ct);
        var blob = c.GetBlobClient(fileName);
        if (!(await blob.ExistsAsync(ct)).Value) return null;

        var result = await blob.DownloadStreamingAsync(new BlobDownloadOptions(), ct);
        return new DocumentDownload(result.Value.Content, DocumentTypes.ContentTypeFor(fileName));
    }
}
