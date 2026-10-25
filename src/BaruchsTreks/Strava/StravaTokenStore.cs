using System.Text.Json;
using System.Text.Json.Nodes;
using BaruchsTreks.Storage;

namespace BaruchsTreks.Strava;

/// <summary>
/// Tokeny uživatele v <see cref="IConfigRepository"/> pod klíčem <c>strava_tokens_{uživatel}</c>. Formát (JSON objekt
/// ze Stravy: <c>access_token</c>, <c>refresh_token</c>, <c>expires_at</c>, <c>athlete</c>…) se nemění, čte ho i Django.
/// Neznámá pole se zachovávají, proto se pracuje s <see cref="JsonObject"/>.
/// </summary>
public sealed class StravaTokenStore(IConfigRepository config)
{
    public static string Key(string userName) => $"strava_tokens_{userName}";

    /// <summary>Uložené tokeny, nebo <c>null</c> (neexistují, jsou prázdné nebo nečitelné).</summary>
    public async Task<JsonObject?> GetAsync(string userName)
    {
        var raw = await config.GetAsync(Key(userName));
        if (raw is null)
            return null;
        try
        {
            return JsonNode.Parse(raw) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task SaveAsync(string userName, JsonObject tokens) => config.SaveAsync(Key(userName), tokens.ToJsonString());

    public Task DeleteAsync(string userName) => config.DeleteAsync(Key(userName));
}
