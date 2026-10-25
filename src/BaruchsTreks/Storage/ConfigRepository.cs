using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Options;

namespace BaruchsTreks.Storage;

/// <summary>Malé klíč/hodnota řádky ve stejné tabulce jako výlety, ale v partition <c>Config</c> (výpis výletů je nevidí).</summary>
public interface IConfigRepository
{
    /// <summary>Hodnota (JSON) nebo <c>null</c>, když řádek neexistuje nebo je prázdný.</summary>
    Task<string?> GetAsync(string key);

    Task SaveAsync(string key, string json);

    /// <summary>Smaže řádek; neexistující je v pořádku.</summary>
    Task DeleteAsync(string key);
}

public sealed class ConfigRepository(TableServiceClient tables, IOptions<StorageOptions> options) : IConfigRepository
{
    public const string PartitionKey = "Config";
    private const string ValueProperty = "Value";

    private readonly TableClient _table = tables.GetTableClient(options.Value.TripsTable);

    public async Task<string?> GetAsync(string key)
    {
        if (!TableKey.IsValid(key))
            return null;
        try
        {
            var result = await _table.GetEntityIfExistsAsync<TableEntity>(PartitionKey, key);
            if (!result.HasValue)
                return null;
            var raw = TableValue.String(result.Value!, ValueProperty);
            return raw.Length == 0 ? null : raw;
        }
        catch (RequestFailedException ex) when (ex.Status is 400 or 404)
        {
            return null;
        }
    }

    public Task SaveAsync(string key, string json)
    {
        if (!TableKey.IsValid(key))
            throw new ArgumentException("Invalid config key.", nameof(key));
        return _table.UpsertEntityAsync(
            new TableEntity(PartitionKey, key) { [ValueProperty] = json }, TableUpdateMode.Replace);
    }

    public Task DeleteAsync(string key) =>
        TableKey.IsValid(key) ? _table.DeleteEntityAsync(PartitionKey, key) : Task.CompletedTask;
}
