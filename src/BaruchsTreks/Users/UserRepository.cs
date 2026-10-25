using Azure;
using Azure.Data.Tables;
using BaruchsTreks.Storage;
using Microsoft.Extensions.Options;

namespace BaruchsTreks.Users;

public interface IUserRepository
{
    /// <summary>Uživatel podle jména (bez ohledu na velikost písmen), nebo <c>null</c>.</summary>
    Task<AppUser?> FindAsync(string userName);

    /// <summary>Založí uživatele, nebo přepíše existujícího se stejným jménem (bez ohledu na velikost písmen).</summary>
    Task UpsertAsync(AppUser u);
}

public sealed class UserRepository(TableServiceClient tables, IOptions<StorageOptions> options) : IUserRepository
{
    private const string UserPartition = "user";

    private readonly TableClient _table = tables.GetTableClient(options.Value.UsersTable);

    public async Task<AppUser?> FindAsync(string userName)
    {
        var rowKey = RowKeyOf(userName);
        if (!TableKey.IsValid(rowKey))
            return null;
        try
        {
            var result = await _table.GetEntityIfExistsAsync<TableEntity>(UserPartition, rowKey);
            return result.HasValue ? FromEntity(result.Value!) : null;
        }
        catch (RequestFailedException ex) when (ex.Status is 400 or 404)
        {
            // Neplatný klíč (např. příliš dlouhé jméno) je pro volajícího totéž co neexistující uživatel.
            return null;
        }
    }

    public async Task UpsertAsync(AppUser u)
    {
        var rowKey = RowKeyOf(u.UserName);
        if (!TableKey.IsValid(rowKey))
            throw new ArgumentException($"User name '{u.UserName}' cannot be used as a table key.", nameof(u));

        var entity = new TableEntity(UserPartition, rowKey)
        {
            ["UserName"] = u.UserName,
            ["PasswordHash"] = u.PasswordHash,
            ["IsAdmin"] = u.IsAdmin,
            ["Created"] = u.Created,
        };
        await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace);
    }

    private static string RowKeyOf(string userName) => userName.ToLowerInvariant();

    private static AppUser FromEntity(TableEntity entity) => new(
        TableValue.String(entity, "UserName", entity.RowKey),
        TableValue.String(entity, "PasswordHash"),
        entity.TryGetValue("IsAdmin", out var v) && v is true,
        TableValue.String(entity, "Created"));
}
