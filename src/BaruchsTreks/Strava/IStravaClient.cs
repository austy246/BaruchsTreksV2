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

/// <summary>
/// Strava odpověděla jiným stavem než 200 (nebo 200 s nečitelným tělem). Výjimka nikdy nenese tělo odpovědi:
/// u 2xx a u nečitelného JSON je <see cref="Body"/> prázdné (tělo token endpointu jsou tokeny), u chyb obsahuje jen
/// <c>message</c> a <c>errors[].code</c> ze Stravy. Díky tomu se <see cref="Body"/> smí zalogovat.
/// </summary>
public sealed class StravaHttpException(int statusCode, string body)
    : Exception($"Strava returned HTTP {statusCode}")
{
    private const int MaxMessageLength = 200;
    private const int MaxCodes = 10;

    public int StatusCode { get; } = statusCode;

    /// <summary>Bezpečné shrnutí chyby (<c>message</c> a kódy), nikdy surové tělo; prázdné pro 2xx.</summary>
    public string Body { get; } = statusCode is >= 200 and < 300 ? "" : Summarize(body);

    private static string Summarize(string body)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                return "";

            var parts = new List<string>();
            if (root.TryGetProperty("message", out var message) && message.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var text = new string((message.GetString() ?? "").Where(c => !char.IsControl(c)).ToArray());
                parts.Add($"message=\"{(text.Length <= MaxMessageLength ? text : text[..MaxMessageLength] + "…")}\"");
            }

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var codes = errors.EnumerateArray()
                    .Where(e => e.ValueKind == System.Text.Json.JsonValueKind.Object
                        && e.TryGetProperty("code", out var code) && code.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(e => new string(e.GetProperty("code").GetString()!.Where(char.IsLetterOrDigit).ToArray()))
                    .Where(c => c.Length > 0)
                    .Take(MaxCodes)
                    .ToList();
                if (codes.Count > 0)
                    parts.Add($"codes=[{string.Join(',', codes)}]");
            }

            return string.Join(' ', parts);
        }
        catch (System.Text.Json.JsonException)
        {
            return "";
        }
    }
}
