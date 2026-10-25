using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BaruchsTreks.Storage;
using BaruchsTreks.Strava;
using BaruchsTreks.Tracks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BaruchsTreks.Tests.Unit;

public class StravaServiceTests
{
    private const string User = "baruch";
    private const string Key = "strava_tokens_baruch";
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static long NowUnix => Now.ToUnixTimeSeconds();

    private sealed class MemoryConfig : IConfigRepository
    {
        public Dictionary<string, string> Rows { get; } = [];
        public Task<string?> GetAsync(string key) => Task.FromResult(Rows.TryGetValue(key, out var v) ? v : null);
        public Task SaveAsync(string key, string json) { Rows[key] = json; return Task.CompletedTask; }
        public Task DeleteAsync(string key) { Rows.Remove(key); return Task.CompletedTask; }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record Rig(StravaService Service, FakeStravaClient Client, MemoryConfig Config);

    private static Rig Create(bool configured = true)
    {
        var client = new FakeStravaClient();
        var config = new MemoryConfig();
        var options = Options.Create(new StravaOptions
        {
            ClientId = configured ? "cid-1" : "",
            ClientSecret = configured ? "secret-1" : "",
        });
        var service = new StravaService(client, new StravaTokenStore(config), options, new FixedClock(Now), NullLogger<StravaService>.Instance);
        return new Rig(service, client, config);
    }

    private static void StoreTokens(Rig rig, long expiresAt) =>
        rig.Config.Rows[Key] =
            "{\"token_type\": \"Bearer\", \"access_token\": \"old-access\", \"refresh_token\": \"old-refresh\", "
            + $"\"expires_at\": {expiresAt}, \"expires_in\": 21600, \"athlete\": {{\"id\": 42, \"firstname\": \"Baruch\"}}}}";

    [Fact]
    public async Task Disabled_WhenNotConfigured()
    {
        var rig = Create(configured: false);

        Assert.False(rig.Service.IsConfigured);
        var ex = Assert.Throws<StravaNotConfigured>(() => rig.Service.AuthorizeUrl("https://x.test/cb", "s"));
        Assert.Equal("Strava není nakonfigurovaná - nastav STRAVA_CLIENT_ID a STRAVA_CLIENT_SECRET.", ex.Message);
        await Assert.ThrowsAsync<StravaNotConfigured>(() => rig.Service.ExchangeCodeAsync("code"));
        Assert.Empty(rig.Client.TokenRequests);
    }

    [Theory]
    [InlineData("", "secret")]
    [InlineData("id", "")]
    public void IsConfigured_NeedsBothValues(string id, string secret)
    {
        var service = new StravaService(new FakeStravaClient(), new StravaTokenStore(new MemoryConfig()),
            Options.Create(new StravaOptions { ClientId = id, ClientSecret = secret }), new FixedClock(Now), NullLogger<StravaService>.Instance);

        Assert.False(service.IsConfigured);
    }

    [Fact]
    public void AuthorizeUrl_HasAllParameters()
    {
        var url = Create().Service.AuthorizeUrl("https://treks.test/trips/strava/callback/", "state-1");

        Assert.StartsWith("https://www.strava.com/oauth/authorize?", url);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        Assert.Equal("cid-1", query["client_id"]);
        Assert.Equal("https://treks.test/trips/strava/callback/", query["redirect_uri"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("auto", query["approval_prompt"]);
        Assert.Equal("activity:read_all", query["scope"]);
        Assert.Equal("state-1", query["state"]);
        Assert.DoesNotContain("secret-1", url);
    }

    [Fact]
    public async Task ExchangeCode_PostsAuthorizationCodeGrant()
    {
        var rig = Create();
        rig.Client.RespondToken("""{"access_token": "a", "refresh_token": "r", "expires_at": 1}""");

        var tokens = await rig.Service.ExchangeCodeAsync("the-code");

        Assert.Equal("a", (string?)tokens["access_token"]);
        var form = Assert.Single(rig.Client.TokenRequests);
        Assert.Equal("cid-1", form["client_id"]);
        Assert.Equal("secret-1", form["client_secret"]);
        Assert.Equal("the-code", form["code"]);
        Assert.Equal("authorization_code", form["grant_type"]);
    }

    [Fact]
    public async Task ExchangeCode_Failure_ReportsStatusButNotBody()
    {
        var rig = Create();
        rig.Client.FailToken(400, "{\"message\":\"secret-1 leaked\"}");

        var ex = await Assert.ThrowsAsync<StravaError>(() => rig.Service.ExchangeCodeAsync("bad"));

        Assert.Equal("Výměna kódu za token selhala (400).", ex.Message);
    }

    [Fact]
    public async Task GetAccessToken_RefreshesWhenStale_AndSaves()
    {
        var rig = Create();
        StoreTokens(rig, NowUnix + 299);
        rig.Client.RespondToken("""{"token_type": "Bearer", "access_token": "new-access", "refresh_token": "new-refresh", "expires_at": 9999999999, "expires_in": 21600}""");

        var access = await rig.Service.GetAccessTokenAsync(User);

        Assert.Equal("new-access", access);
        var form = Assert.Single(rig.Client.TokenRequests);
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("old-refresh", form["refresh_token"]);
        Assert.Equal("cid-1", form["client_id"]);
        Assert.Equal("secret-1", form["client_secret"]);

        // Uloženo sloučeně: nové tokeny, ale údaje o sportovci zůstávají (Strava je při obnovení nevrací).
        var saved = JsonNode.Parse(rig.Config.Rows[Key])!.AsObject();
        Assert.Equal("new-access", (string?)saved["access_token"]);
        Assert.Equal("new-refresh", (string?)saved["refresh_token"]);
        Assert.Equal(9999999999, (long?)saved["expires_at"]);
        Assert.Equal(42, (int?)saved["athlete"]!["id"]);
    }

    [Fact]
    public async Task GetAccessToken_AtMarginBoundary_DoesNotRefresh()
    {
        var rig = Create();
        StoreTokens(rig, NowUnix + StravaService.RefreshMarginSeconds);

        Assert.Equal(300, StravaService.RefreshMarginSeconds);
        Assert.Equal("old-access", await rig.Service.GetAccessTokenAsync(User));
        Assert.Empty(rig.Client.TokenRequests);
    }

    [Fact]
    public async Task GetAccessToken_MissingExpiresAt_Refreshes()
    {
        var rig = Create();
        rig.Config.Rows[Key] = """{"access_token": "old", "refresh_token": "old-refresh"}""";
        rig.Client.RespondToken("""{"access_token": "fresh", "refresh_token": "r2", "expires_at": 9999999999}""");

        Assert.Equal("fresh", await rig.Service.GetAccessTokenAsync(User));
    }

    [Fact]
    public async Task GetAccessToken_NotConnected_Throws()
    {
        var ex = await Assert.ThrowsAsync<StravaNotConnected>(() => Create().Service.GetAccessTokenAsync(User));

        Assert.Equal("Účet Strava není propojený.", ex.Message);
    }

    [Fact]
    public async Task GetAccessToken_RefreshFailure_IsStravaError()
    {
        var rig = Create();
        StoreTokens(rig, NowUnix - 10);
        rig.Client.FailToken(400);

        var ex = await Assert.ThrowsAsync<StravaError>(() => rig.Service.GetAccessTokenAsync(User));

        Assert.Equal("Obnovení tokenu selhalo (400).", ex.Message);
        // Neúspěšná obnova nesmí přepsat uložené tokeny.
        Assert.Contains("old-refresh", rig.Config.Rows[Key]);
    }

    [Fact]
    public async Task ListActivities_MapsFields()
    {
        var rig = Create();
        StoreTokens(rig, NowUnix + 3600);
        rig.Client.Respond("/athlete/activities", """
            [
              {"id": 111, "name": "Sněžka", "sport_type": "Hike", "type": "Walk", "start_date_local": "2026-06-30T06:52:54Z",
               "distance": 12345.6, "total_elevation_gain": 812.5, "elapsed_time": 14400, "start_latlng": [50.7, 15.7]},
              {"id": 222, "name": "Trenažér", "type": "Ride", "start_date_local": "2026-06-29T18:00:00+02:00",
               "distance": 0, "total_elevation_gain": null, "elapsed_time": 5400, "start_latlng": []},
              {"id": 333}
            ]
            """);

        var activities = await rig.Service.ListActivitiesAsync(User, page: 3, perPage: 10);

        var call = Assert.Single(rig.Client.GetRequests);
        Assert.Equal("old-access", call.AccessToken);
        Assert.Equal("3", call.Query["page"]);
        Assert.Equal("10", call.Query["per_page"]);

        Assert.Equal(3, activities.Count);
        var hike = activities[0];
        Assert.Equal(111, hike.Id);
        Assert.Equal("Sněžka", hike.Name);
        Assert.Equal("Hike", hike.SportType);
        Assert.Equal(new DateOnly(2026, 6, 30), hike.StartDate);
        Assert.Equal("2026-06-30T06:52:54Z", hike.StartDateLocal);
        Assert.Equal(12.35, hike.DistanceKm);
        Assert.Equal(812, hike.ElevationGain);
        Assert.Equal(4.0, hike.ElapsedHours);
        Assert.True(hike.HasGps);

        var ride = activities[1];
        Assert.Equal("Ride", ride.SportType);
        Assert.Equal(new DateOnly(2026, 6, 29), ride.StartDate);
        Assert.Equal(0, ride.DistanceKm);
        Assert.Equal(0, ride.ElevationGain);
        Assert.Equal(1.5, ride.ElapsedHours);
        Assert.False(ride.HasGps);

        var bare = activities[2];
        Assert.Equal("", bare.Name);
        Assert.Equal("", bare.SportType);
        Assert.Null(bare.StartDate);
        Assert.False(bare.HasGps);
    }

    [Fact]
    public async Task ListActivities_DefaultsToFirstPageOfThirty()
    {
        var rig = Create();
        StoreTokens(rig, NowUnix + 3600);
        rig.Client.Respond("/athlete/activities", "[]");

        await rig.Service.ListActivitiesAsync(User);

        var call = Assert.Single(rig.Client.GetRequests);
        Assert.Equal("1", call.Query["page"]);
        Assert.Equal("30", call.Query["per_page"]);
    }

    [Fact]
    public async Task Api401_ReportsReconnect()
    {
        var rig = Create();
        StoreTokens(rig, NowUnix + 3600);
        rig.Client.Fail("/athlete/activities", 401);

        var ex = await Assert.ThrowsAsync<StravaNotConnected>(() => rig.Service.ListActivitiesAsync(User));

        Assert.Equal("Přístup k účtu Strava vypršel, propoj ho prosím znovu.", ex.Message);
    }

    [Fact]
    public async Task Api429_ReportsRateLimit()
    {
        var rig = Create();
        StoreTokens(rig, NowUnix + 3600);
        rig.Client.Fail("/athlete/activities", 429);

        var ex = await Assert.ThrowsAsync<StravaError>(() => rig.Service.ListActivitiesAsync(User));

        Assert.Equal("Překročen limit požadavků na Stravu, zkus to prosím za chvíli.", ex.Message);
    }

    [Fact]
    public async Task ApiOtherError_ReportsStatusOnly()
    {
        var rig = Create();
        StoreTokens(rig, NowUnix + 3600);
        rig.Client.Fail("/athlete/activities", 500, "{\"message\":\"boom old-access\"}");

        var ex = await Assert.ThrowsAsync<StravaError>(() => rig.Service.ListActivitiesAsync(User));

        Assert.Equal("Strava vrátila chybu 500.", ex.Message);
    }

    [Fact]
    public async Task ImportGpx_BuildsPointsFromStreams()
    {
        var rig = Create();
        StoreTokens(rig, NowUnix + 3600);
        rig.Client.Respond("/activities/777", """
            {"id": 777, "name": "Ranní <túra>", "sport_type": "Hike", "start_date_local": "2026-06-30T06:00:00Z",
             "distance": 1000, "total_elevation_gain": 20, "elapsed_time": 120, "start_latlng": [50.0, 14.0]}
            """);
        // Výška chybí u posledního bodu, čas má jen první dva (kratší streamy se tolerují).
        rig.Client.Respond("/activities/777/streams", """
            {"latlng": {"data": [[50.0, 14.0], [50.001, 14.0], [50.002, 14.0]]},
             "altitude": {"data": [400.0, 410.5]},
             "time": {"data": [0, 60]}}
            """);

        var result = await rig.Service.ImportGpxAsync(User, 777);

        var streams = rig.Client.GetRequests.Single(r => r.Path == "/activities/777/streams");
        Assert.Equal("latlng,altitude,time", streams.Query["keys"]);
        Assert.Equal("true", streams.Query["key_by_type"]);
        Assert.Equal(777, result.Activity.Id);

        var expected = GpxBuilder.Build("Ranní <túra>",
        [
            new TrackPoint(50.0, 14.0, 400.0, new DateTimeOffset(2026, 6, 30, 6, 0, 0, TimeSpan.Zero)),
            new TrackPoint(50.001, 14.0, 410.5, new DateTimeOffset(2026, 6, 30, 6, 1, 0, TimeSpan.Zero)),
            new TrackPoint(50.002, 14.0, null, null),
        ], new DateTimeOffset(2026, 6, 30, 6, 0, 0, TimeSpan.Zero));
        Assert.Equal(Encoding.UTF8.GetString(expected), Encoding.UTF8.GetString(result.Gpx));

        // A výsledek projde stejným parserem jako nahraný soubor.
        var parsed = GpxParser.Parse(result.Gpx);
        Assert.Equal(3, parsed.Stats.OriginalPointCount);
        Assert.Equal("2026-06-30", parsed.Stats.StartedOn);
    }

    [Fact]
    public async Task ImportGpx_UnnamedActivity_UsesStravaId()
    {
        var rig = Create();
        StoreTokens(rig, NowUnix + 3600);
        rig.Client.Respond("/activities/5", """{"id": 5, "name": "", "start_latlng": [1, 2]}""");
        rig.Client.Respond("/activities/5/streams", """{"latlng": {"data": [[50.0, 14.0]]}}""");

        var result = await rig.Service.ImportGpxAsync(User, 5);

        Assert.Contains("<name>Strava 5</name>", Encoding.UTF8.GetString(result.Gpx));
        Assert.DoesNotContain("<time>", Encoding.UTF8.GetString(result.Gpx));
    }

    [Fact]
    public async Task Import_NoGps_ShowsError()
    {
        var rig = Create();
        StoreTokens(rig, NowUnix + 3600);
        rig.Client.Respond("/activities/9", """{"id": 9, "name": "Pás", "start_latlng": []}""");
        rig.Client.Respond("/activities/9/streams", """{"time": {"data": [0, 1]}}""");

        var ex = await Assert.ThrowsAsync<StravaError>(() => rig.Service.ImportGpxAsync(User, 9));

        Assert.Equal("Aktivita neobsahuje GPS data, nelze z ní vytvořit trasu.", ex.Message);
    }

    [Fact]
    public async Task Tokens_RoundTripThroughConfigRowFormat()
    {
        var rig = Create();
        rig.Client.RespondToken("""{"token_type":"Bearer","access_token":"a","refresh_token":"r","expires_at":123,"expires_in":21600,"athlete":{"id":1}}""");

        await rig.Service.ConnectAsync(User, "code");

        Assert.True(await rig.Service.IsConnectedAsync(User));
        using var doc = JsonDocument.Parse(rig.Config.Rows[Key]);
        Assert.Equal("a", doc.RootElement.GetProperty("access_token").GetString());
        Assert.Equal(123, doc.RootElement.GetProperty("expires_at").GetInt64());
        Assert.Equal(1, doc.RootElement.GetProperty("athlete").GetProperty("id").GetInt32());

        await rig.Service.DisconnectAsync(User);
        Assert.False(await rig.Service.IsConnectedAsync(User));
    }

    [Fact]
    public async Task UnreadableStoredTokens_CountAsNotConnected()
    {
        var rig = Create();
        rig.Config.Rows[Key] = "not json";

        Assert.False(await rig.Service.IsConnectedAsync(User));
    }
}
