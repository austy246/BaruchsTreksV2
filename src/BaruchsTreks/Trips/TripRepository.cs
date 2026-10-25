using Azure;
using Azure.Data.Tables;
using BaruchsTreks.Storage;
using Microsoft.Extensions.Options;

namespace BaruchsTreks.Trips;

public interface ITripRepository
{
    /// <summary>Všechny výlety, nejnověji změněné první (<c>Timestamp</c>, jinak čas z RowKey, jinak nejstarší).</summary>
    Task<IReadOnlyList<Trip>> ListAsync();

    /// <summary>Výlet podle id, nebo <c>null</c> (neexistuje i podivný klíč).</summary>
    Task<Trip?> GetAsync(string id);

    /// <summary>Založí výlet a vrátí jeho id (<c>trip_yyyyMMddHHmmss</c>).</summary>
    Task<string> CreateAsync(TripFormData data);

    /// <summary>Merge poslaných polí; ostatní vlastnosti i typy zůstanou. Neexistující id: <see cref="KeyNotFoundException"/>.</summary>
    Task UpdateAsync(string id, TripFormData data);

    /// <summary>Merge jednotlivých vlastností (např. <c>TrackJson</c> = "" při smazání trasy). Neexistující id: <see cref="KeyNotFoundException"/>.</summary>
    Task MergeFieldsAsync(string id, IReadOnlyDictionary<string, object?> fields);

    /// <summary>Smaže výlet; neexistující je v pořádku.</summary>
    Task DeleteAsync(string id);
}

public sealed class TripRepository(TableServiceClient tables, IOptions<StorageOptions> options) : ITripRepository
{
    private const string PartitionFilter = "PartitionKey eq 'Trips'";
    private const int MaxKeyAttempts = 30;

    private readonly TableClient _table = tables.GetTableClient(options.Value.TripsTable);

    public async Task<IReadOnlyList<Trip>> ListAsync()
    {
        var trips = new List<Trip>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(PartitionFilter))
        {
            if (!string.IsNullOrEmpty(entity.RowKey))
                trips.Add(TripEntityMapper.FromEntity(entity));
        }
        return TripListing.Sort(trips, "modified", "desc");
    }

    public async Task<Trip?> GetAsync(string id)
    {
        var entity = await GetEntityAsync(id);
        return entity is null ? null : TripEntityMapper.FromEntity(entity);
    }

    public async Task<string> CreateAsync(TripFormData data)
    {
        var now = DateTimeOffset.UtcNow;
        // RowKey má přesnost na sekundu, takže dva výlety v jedné sekundě se srazí; posuneme o další sekundu.
        for (var attempt = 0; attempt < MaxKeyAttempts; attempt++)
        {
            var rowKey = TripEntityMapper.NewRowKey(now.AddSeconds(attempt));
            try
            {
                await _table.AddEntityAsync(TripEntityMapper.ToNewEntity(rowKey, data));
                return rowKey;
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
            }
        }
        throw new InvalidOperationException("Could not allocate a unique trip id.");
    }

    public async Task UpdateAsync(string id, TripFormData data)
    {
        var existing = await GetEntityAsync(id) ?? throw new KeyNotFoundException($"Trip '{id}' does not exist.");
        await _table.UpdateEntityAsync(TripEntityMapper.ToMergeEntity(existing, data), ETag.All, TableUpdateMode.Merge);
    }

    public async Task MergeFieldsAsync(string id, IReadOnlyDictionary<string, object?> fields)
    {
        var existing = await GetEntityAsync(id) ?? throw new KeyNotFoundException($"Trip '{id}' does not exist.");
        await _table.UpdateEntityAsync(TripEntityMapper.ToMergeEntity(existing, fields), ETag.All, TableUpdateMode.Merge);
    }

    public async Task DeleteAsync(string id)
    {
        if (!TableKey.IsValid(id))
            return;
        await _table.DeleteEntityAsync(TripEntityMapper.PartitionKey, id);
    }

    private async Task<TableEntity?> GetEntityAsync(string id)
    {
        if (!TableKey.IsValid(id))
            return null;
        try
        {
            var result = await _table.GetEntityIfExistsAsync<TableEntity>(TripEntityMapper.PartitionKey, id);
            return result.HasValue ? result.Value : null;
        }
        catch (RequestFailedException ex) when (ex.Status is 400 or 404)
        {
            // Neplatný klíč je pro volajícího totéž co neexistující záznam.
            return null;
        }
    }
}
