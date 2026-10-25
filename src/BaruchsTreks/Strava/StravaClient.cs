using System.Text.Json;

namespace BaruchsTreks.Strava;

/// <summary>Skutečná komunikace se Stravou přes <see cref="HttpClient"/>. Jediná třída, která opouští proces.</summary>
public sealed class StravaClient(HttpClient http) : IStravaClient
{
    public const string TokenUrl = "https://www.strava.com/oauth/token";
    public const string ApiBase = "https://www.strava.com/api/v3";

    /// <summary>Stejný limit jako <c>REQUEST_TIMEOUT</c> v Pythonu.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public async Task<JsonDocument> PostTokenAsync(IDictionary<string, string> form)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(TokenUrl, content);
        return await ReadAsync(response);
    }

    public async Task<JsonDocument> GetAsync(string accessToken, string path, IDictionary<string, string>? query)
    {
        var url = ApiBase + path;
        if (query is { Count: > 0 })
        {
            url += "?" + string.Join('&', query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request);
        return await ReadAsync(response);
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        // Jako Python: cokoli kromě 200 je chyba.
        if ((int)response.StatusCode != 200)
            throw new StravaHttpException((int)response.StatusCode, body);
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // Nečitelné tělo 200 (token endpoint = tokeny) se dál nenese ani nezapisuje do logu: výjimka dostane prázdné tělo.
            throw new StravaHttpException((int)response.StatusCode, "");
        }
    }
}

public static class StravaServiceCollectionExtensions
{
    /// <summary>Zaregistruje skutečného klienta Stravy s timeoutem 30 s. Testy ho nahrazují falešným.</summary>
    public static IServiceCollection AddStravaClient(this IServiceCollection services)
    {
        services.AddHttpClient<IStravaClient, StravaClient>(client => client.Timeout = StravaClient.RequestTimeout);
        return services;
    }
}
