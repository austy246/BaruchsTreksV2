using System.Text.Json;

namespace BaruchsTreks.Strava;

/// <summary>
/// Jediné místo, kudy jde provoz ke Stravě. Testy ho nahrazují falešnou implementací, takže test host nikdy nevolá
/// skutečnou síť. Odpověď jiná než 200 se hlásí výjimkou <see cref="StravaHttpException"/>.
/// </summary>
public interface IStravaClient
{
    /// <summary>POST na token endpoint (<c>application/x-www-form-urlencoded</c>); vrací JSON s tokeny.</summary>
    Task<JsonDocument> PostTokenAsync(IDictionary<string, string> form);

    /// <summary>GET na API v3 s <c>Authorization: Bearer</c>; <paramref name="path"/> začíná lomítkem (<c>/athlete/activities</c>).</summary>
    Task<JsonDocument> GetAsync(string accessToken, string path, IDictionary<string, string>? query);
}

/// <summary>Strava odpověděla jiným stavem než 200. Tělo je jen pro log (neobsahuje tokeny), do zprávy se nedává.</summary>
public sealed class StravaHttpException(int statusCode, string body)
    : Exception($"Strava returned HTTP {statusCode}")
{
    public int StatusCode { get; } = statusCode;

    public string Body { get; } = body;
}
