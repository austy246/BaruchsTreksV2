using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using BaruchsTreks.Tracks;
using Microsoft.Extensions.Options;

namespace BaruchsTreks.Strava;

/// <summary>Aktivita zúžená na pole, která potřebuje seznam a import (<c>_summarize_activity</c>).</summary>
public sealed record StravaActivity(
    long Id,
    string Name,
    string SportType,
    DateOnly? StartDate,
    string? StartDateLocal,
    double DistanceKm,
    int ElevationGain,
    double ElapsedHours,
    bool HasGps);

/// <summary>GPX sestavený ze streamů aktivity spolu s jejím shrnutím.</summary>
public sealed record StravaGpx(byte[] Gpx, StravaActivity Activity);

/// <summary>
/// Strava v3 API a uložení tokenů (<c>trips/strava_service.py</c>). Čte jen aktivity připojeného uživatele.
/// Veškerý síťový provoz jde přes <see cref="IStravaClient"/>. Tokeny a secret se nikdy nelogují a nejsou ve zprávách.
/// </summary>
public sealed class StravaService(
    IStravaClient client,
    StravaTokenStore tokens,
    IOptions<StravaOptions> options,
    TimeProvider clock,
    ILogger<StravaService> logger)
{
    public const string AuthorizeBase = "https://www.strava.com/oauth/authorize";

    // activity:read_all pokrývá i aktivity, které sportovec označil jako soukromé.
    public const string Scope = "activity:read_all";

    // Obnovit trochu před vypršením, aby dlouhý požadavek nezávodil s koncem platnosti.
    public const int RefreshMarginSeconds = 300;

    // Společný pro všechny instance (služba je scoped): souběžné požadavky nemají obnovovat tokeny dvakrát.
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);

    private string ClientId => options.Value.ClientId;

    private string ClientSecret => options.Value.ClientSecret;

    public bool IsConfigured => !string.IsNullOrEmpty(ClientId) && !string.IsNullOrEmpty(ClientSecret);

    private void RequireConfigured()
    {
        if (!IsConfigured)
            throw new StravaNotConfigured("Strava není nakonfigurovaná - nastav STRAVA_CLIENT_ID a STRAVA_CLIENT_SECRET.");
    }

    // ----- OAuth -----

    /// <summary>Adresa, kam se uživatel pošle udělit přístup.</summary>
    public string AuthorizeUrl(string redirectUri, string state)
    {
        RequireConfigured();
        var parameters = new (string Key, string Value)[]
        {
            ("client_id", ClientId),
            ("redirect_uri", redirectUri),
            ("response_type", "code"),
            ("approval_prompt", "auto"),
            ("scope", Scope),
            ("state", state),
        };
        return AuthorizeBase + "?" + string.Join('&', parameters.Select(p => $"{WebUtility.UrlEncode(p.Key)}={WebUtility.UrlEncode(p.Value)}"));
    }

    /// <summary>Vymění autorizační kód za access a refresh token.</summary>
    public async Task<JsonObject> ExchangeCodeAsync(string code)
    {
        RequireConfigured();
        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
        };
        return await PostTokenAsync(form, "Výměna kódu za token selhala");
    }

    /// <summary>Vymění kód za tokeny a uloží je uživateli.</summary>
    public async Task ConnectAsync(string userName, string code) =>
        await tokens.SaveAsync(userName, await ExchangeCodeAsync(code));

    private async Task<JsonObject> RefreshAsync(JsonObject current)
    {
        RequireConfigured();
        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["refresh_token"] = (string?)current["refresh_token"] ?? "",
            ["grant_type"] = "refresh_token",
        };
        var refreshed = await PostTokenAsync(form, "Obnovení tokenu selhalo");

        // Strava při obnovení vrací jen pole tokenů, takže údaje o sportovci zůstávají z původních.
        var merged = (JsonObject)current.DeepClone();
        foreach (var (key, value) in refreshed)
            merged[key] = value?.DeepClone();
        return merged;
    }

    private async Task<JsonObject> PostTokenAsync(Dictionary<string, string> form, string failure)
    {
        try
        {
            using var response = await client.PostTokenAsync(form);
            return JsonNode.Parse(response.RootElement.GetRawText()) as JsonObject
                ?? throw new StravaError($"{failure}.");
        }
        catch (StravaHttpException ex)
        {
            // Tělo odpovědi jen do logu a zkrácené; uživateli stačí stav.
            logger.LogWarning("Strava token request failed with HTTP {Status}: {Body}", ex.StatusCode, Truncate(ex.Body));
            throw new StravaError($"{failure} ({ex.StatusCode}).");
        }
        catch (Exception ex) when (IsNetworkFailure(ex))
        {
            logger.LogWarning(ex, "Strava token request failed");
            throw new StravaError("Spojení se Stravou selhalo, zkus to prosím za chvíli.");
        }
    }

    // ----- uložení tokenů -----

    public async Task<bool> IsConnectedAsync(string userName) => await tokens.GetAsync(userName) is not null;

    public Task DisconnectAsync(string userName) => tokens.DeleteAsync(userName);

    /// <summary>Platný access token; zastaralý se nejdřív obnoví a uloží.</summary>
    public async Task<string> GetAccessTokenAsync(string userName)
    {
        var current = await tokens.GetAsync(userName) ?? throw NotConnected();
        if (IsStale(current))
        {
            // Dva souběžné požadavky by obnovovaly zbytečně dvakrát; druhý uvidí už uložené nové tokeny.
            await RefreshLock.WaitAsync();
            try
            {
                current = await tokens.GetAsync(userName) ?? throw NotConnected();
                if (IsStale(current))
                {
                    logger.LogInformation("Refreshing Strava token for {UserName}", userName);
                    current = await RefreshAsync(current);
                    await tokens.SaveAsync(userName, current);
                }
            }
            finally
            {
                RefreshLock.Release();
            }
        }

        return current["access_token"] is JsonValue token && token.TryGetValue<string>(out var access) && access.Length > 0
            ? access
            : throw NotConnected();
    }

    private bool IsStale(JsonObject current)
    {
        // Chybějící expires_at se bere jako 0, tedy vypršelo.
        var expiresAt = current["expires_at"] is JsonValue v && v.TryGetValue<double>(out var seconds) ? seconds : 0;
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
        return expiresAt - now < RefreshMarginSeconds;
    }

    private static StravaNotConnected NotConnected() => new("Účet Strava není propojený.");

    // ----- API -----

    private async Task<JsonElement> GetAsync(string userName, string path, Dictionary<string, string>? query = null)
    {
        var accessToken = await GetAccessTokenAsync(userName);
        try
        {
            using var response = await client.GetAsync(accessToken, path, query);
            return response.RootElement.Clone();
        }
        catch (StravaHttpException ex)
        {
            if (ex.StatusCode == 401)
                throw new StravaNotConnected("Přístup k účtu Strava vypršel, propoj ho prosím znovu.");
            if (ex.StatusCode == 429)
                throw new StravaError("Překročen limit požadavků na Stravu, zkus to prosím za chvíli.");
            logger.LogWarning("Strava API {Path} failed with HTTP {Status}: {Body}", path, ex.StatusCode, Truncate(ex.Body));
            throw new StravaError($"Strava vrátila chybu {ex.StatusCode}.");
        }
        catch (Exception ex) when (IsNetworkFailure(ex))
        {
            logger.LogWarning(ex, "Strava API {Path} request failed", path);
            throw new StravaError("Spojení se Stravou selhalo, zkus to prosím za chvíli.");
        }
    }

    /// <summary>Vlastní aktivity sportovce, nejnovější první.</summary>
    public async Task<IReadOnlyList<StravaActivity>> ListActivitiesAsync(string userName, int page = 1, int perPage = 30)
    {
        var query = new Dictionary<string, string>
        {
            ["page"] = page.ToString(CultureInfo.InvariantCulture),
            ["per_page"] = perPage.ToString(CultureInfo.InvariantCulture),
        };
        var activities = await GetAsync(userName, "/athlete/activities", query);
        if (activities.ValueKind != JsonValueKind.Array)
            throw new StravaError("Strava vrátila neočekávanou odpověď.");
        return activities.EnumerateArray().Select(Summarize).ToList();
    }

    public async Task<StravaActivity> GetActivityAsync(string userName, long activityId) =>
        Summarize(await GetAsync(userName, $"/activities/{activityId}"));

    /// <summary>Stáhne streamy aktivity a sestaví z nich GPX (stejný formát jako ruční export z Garminu).</summary>
    /// <exception cref="StravaError">Aktivita nemá GPS data.</exception>
    public async Task<StravaGpx> ImportGpxAsync(string userName, long activityId)
    {
        var activityJson = await GetAsync(userName, $"/activities/{activityId}");
        var activity = Summarize(activityJson);

        var streams = await GetAsync(userName, $"/activities/{activityId}/streams", new Dictionary<string, string>
        {
            ["keys"] = "latlng,altitude,time",
            ["key_by_type"] = "true",
        });

        var latlng = StreamData(streams, "latlng");
        if (latlng.Count == 0)
            throw new StravaError("Aktivita neobsahuje GPS data, nelze z ní vytvořit trasu.");

        var altitude = StreamData(streams, "altitude");
        var offsets = StreamData(streams, "time");

        var startTime = ParseTime(activity.StartDateLocal);
        var points = new List<TrackPoint>(latlng.Count);
        for (var index = 0; index < latlng.Count; index++)
        {
            if (!TryCoordinates(latlng[index], out var lat, out var lng))
                throw new StravaError("Strava vrátila neplatná GPS data.");

            double? elevation = index < altitude.Count && altitude[index].ValueKind == JsonValueKind.Number
                ? altitude[index].GetDouble()
                : null;
            DateTimeOffset? pointTime = null;
            if (startTime is { } start && index < offsets.Count && offsets[index].ValueKind == JsonValueKind.Number)
                pointTime = start.AddSeconds(offsets[index].GetDouble());
            points.Add(new TrackPoint(lat, lng, elevation, pointTime));
        }

        logger.LogInformation("Built GPX from Strava activity {ActivityId} with {Count} points", activityId, points.Count);
        var name = activity.Name.Length > 0 ? activity.Name : $"Strava {activityId}";
        return new StravaGpx(GpxBuilder.Build(name, points, startTime), activity);
    }

    private static List<JsonElement> StreamData(JsonElement streams, string key) =>
        streams.ValueKind == JsonValueKind.Object
        && streams.TryGetProperty(key, out var stream)
        && stream.ValueKind == JsonValueKind.Object
        && stream.TryGetProperty("data", out var data)
        && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().ToList()
            : [];

    private static bool TryCoordinates(JsonElement point, out double lat, out double lng)
    {
        lat = lng = 0;
        if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 2)
            return false;
        var pair = point.EnumerateArray().ToList();
        return pair[0].ValueKind == JsonValueKind.Number && pair[1].ValueKind == JsonValueKind.Number
            && pair[0].TryGetDouble(out lat) && pair[1].TryGetDouble(out lng);
    }

    // ----- shrnutí aktivity -----

    internal static StravaActivity Summarize(JsonElement activity)
    {
        var startLocal = Text(activity, "start_date_local");
        DateOnly? startDate = ParseTime(startLocal) is { } start ? DateOnly.FromDateTime(start.DateTime) : null;

        var sportType = Text(activity, "sport_type");
        if (sportType.Length == 0)
            sportType = Text(activity, "type");

        return new StravaActivity(
            Id: activity.ValueKind == JsonValueKind.Object && activity.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var value) ? value : 0,
            Name: Text(activity, "name"),
            SportType: sportType,
            StartDate: startDate,
            StartDateLocal: startLocal.Length > 0 ? startLocal : null,
            DistanceKm: Math.Round(Number(activity, "distance") / 1000.0, 2),
            ElevationGain: (int)Math.Round(Number(activity, "total_elevation_gain")),
            ElapsedHours: Math.Round(Number(activity, "elapsed_time") / 3600.0, 2),
            HasGps: activity.ValueKind == JsonValueKind.Object
                && activity.TryGetProperty("start_latlng", out var latlng)
                && latlng.ValueKind == JsonValueKind.Array
                && latlng.GetArrayLength() > 0);
    }

    private static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static double Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : 0;

    /// <summary>ISO čas jako Python <c>fromisoformat</c> (<c>Z</c> = UTC); bez posunu se bere jako UTC.</summary>
    private static DateTimeOffset? ParseTime(string? text) =>
        !string.IsNullOrEmpty(text)
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private static bool IsNetworkFailure(Exception ex) =>
        ex is HttpRequestException || (ex is TaskCanceledException && ex.InnerException is TimeoutException);

    private static string Truncate(string body) => body.Length <= 300 ? body : body[..300] + "…";
}
