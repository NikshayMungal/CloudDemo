using Azure.Data.Tables;

namespace CoffeeNChill.Functions.Services;

/// <summary>
/// Base class for Azure Table repositories. The table is created lazily (and only once) the first
/// time it is needed, so the function app can start before Azurite is fully ready.
/// </summary>
public abstract class TableRepositoryBase
{
    private readonly TableClient _table;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _ready;

    protected TableRepositoryBase(TableServiceClient serviceClient, string tableName)
    {
        _table = serviceClient.GetTableClient(tableName);
    }

    protected async Task<TableClient> GetTableAsync(CancellationToken ct)
    {
        if (_ready) return _table;

        await _gate.WaitAsync(ct);
        try
        {
            if (!_ready)
            {
                await _table.CreateIfNotExistsAsync(ct);
                _ready = true;
            }
        }
        finally
        {
            _gate.Release();
        }
        return _table;
    }
}
