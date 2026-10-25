using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BaruchsTreks.Storage;
using BaruchsTreks.Strava;
using BaruchsTreks.Tracks;
using BaruchsTreks.Trips;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BaruchsTreks.Tests.Integration;

public partial class StravaTests(AzuriteFixture azurite)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Tokens =
        "{\"token_type\": \"Bearer\", \"access_token\": \"stored-access-token\", \"refresh_token\": \"stored-refresh-token\", "
        + "\"expires_at\": 9999999999, \"expires_in\": 21600, \"athlete\": {\"id\": 42}}";

    private const string ActivitiesJson = """
        [
          {"id": 111, "name": "Sněžka <&>", "sport_type": "Hike", "start_date_local": "2026-06-30T06:52:54Z",
           "distance": 12345.6, "total_elevation_gain": 812.5, "elapsed_time": 14400, "start_latlng": [50.7, 15.7]},
          {"id": 222, "name": "Běžecký pás", "type": "Run", "start_date_local": "2026-06-29T18:00:00Z",
           "distance": 5000, "total_elevation_gain": 0, "elapsed_time": 1800, "start_latlng": []}
        ]
        """;

    private static string Decoded(string html) => WebUtility.HtmlDecode(html);

    private static AppFactory Configured(AzuriteFixture azurite) =>
        new AppFactory(azurite).WithSetting("Strava:ClientId", "client-id-1").WithSetting("Strava:ClientSecret", "client-secret-1");

    private static string UniqueName() => "admin-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Přihlášený admin, který už má Stravu propojenou (uložené tokeny).</summary>
    private static async Task<(HttpClient Client, string UserName)> ConnectedAdminAsync(AppFactory factory, string tokens = Tokens)
    {
        var userName = UniqueName();
        var client = await factory.CreateUserClientAsync(isAdmin: true, userName);
        await factory.Services.GetRequiredService<IConfigRepository>().SaveAsync("strava_tokens_" + userName, tokens);
        return (client, userName);
    }

    private static void SetUpImportableActivity(AppFactory factory, long id = 777, string sportType = "Hike")
    {
        factory.Strava()
            // Formulář importu bere antiforgery token ze stránky se seznamem.
            .Respond("/athlete/activities", "[]")
            .Respond($"/activities/{id}", $$"""
                {"id": {{id}}, "name": "Ranní túra", "sport_type": "{{sportType}}", "start_date_local": "2026-06-30T06:00:00Z",
                 "distance": 330, "total_elevation_gain": 50, "elapsed_time": 180, "start_latlng": [50.0, 14.0]}
                """)
            .Respond($"/activities/{id}/streams", """
                {"latlng": {"data": [[50.0, 14.0], [50.001, 14.0], [50.002, 14.0], [50.003, 14.0]]},
                 "altitude": {"data": [400.0, 420.0, 450.0, 440.0]},
                 "time": {"data": [0, 60, 120, 180]}}
                """);
    }

    private static async Task<HttpResponseMessage> PostImportAsync(HttpClient client, long id, string? tripId = null)
    {
        var form = tripId is null ? Array.Empty<(string, string)>() : [("trip_id", tripId)];
        return await client.PostFormAsync($"/trips/strava/import/{id}/", "/trips/strava/", form);
    }

    private static async Task<string> PageAsync(HttpClient client, string url = "/trips/strava/") =>
        Decoded(await client.GetStringAsync(url, Ct));

    // ----- vypnuto / zapnuto -----

    [Fact]
    public async Task Disabled_WhenNotConfigured()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        var html = await PageAsync(admin);

        Assert.Contains("Strava zatím není nastavená", html);
        Assert.Contains("STRAVA_CLIENT_ID", html);
        Assert.Contains("STRAVA_CLIENT_SECRET", html);
        Assert.Contains("strava.com/settings/api", html);
        Assert.Contains("Do té doby jde GPX nahrát ručně v editaci tripu", html);
        Assert.DoesNotContain("Propojit se Stravou", html);

        // Connect bez nastavení nikam nepřesměruje, jen vrátí na stránku s hláškou.
        var connect = await admin.GetAsync("/trips/strava/connect/", Ct);
        Assert.Equal(HttpStatusCode.Found, connect.StatusCode);
        Assert.Equal("/trips/strava/", connect.Headers.Location!.OriginalString);
        Assert.Contains("Strava není nakonfigurovaná - nastav STRAVA_CLIENT_ID a STRAVA_CLIENT_SECRET.", await PageAsync(admin));
        Assert.Empty(factory.Strava().TokenRequests);
        Assert.Empty(factory.Strava().GetRequests);
    }

    [Fact]
    public async Task NotConnected_OffersConnectLink_WithTripId()
    {
        await using var factory = Configured(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        var plain = await PageAsync(admin);
        var withTrip = await PageAsync(admin, "/trips/strava/?trip_id=trip_20240101000000");

        Assert.Contains("Propoj svůj účet na Stravě a načti odtud zaznamenané trasy.", plain);
        Assert.Contains("href=\"/trips/strava/connect/\"", plain);
        Assert.Contains("Propojit se Stravou", plain);
        Assert.DoesNotContain("Odpojit Stravu", plain);
        Assert.Contains("href=\"/trips/strava/connect/?trip_id=trip_20240101000000\"", withTrip);
        Assert.Contains("Vybraná trasa se připojí k existujícímu tripu", withTrip);
        Assert.Contains("href=\"/trips/trip/trip_20240101000000/edit/\"", withTrip);
        Assert.Empty(factory.Strava().GetRequests);
    }

    // ----- OAuth -----

    [Fact]
    public async Task Connect_RedirectsToStravaAuthorize_WithRandomState()
    {
        await using var factory = Configured(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        var first = await admin.GetAsync("/trips/strava/connect/", Ct);
        var second = await admin.GetAsync("/trips/strava/connect/", Ct);

        Assert.Equal(HttpStatusCode.Found, first.StatusCode);
        var location = first.Headers.Location!;
        Assert.Equal("https://www.strava.com/oauth/authorize", location.GetLeftPart(UriPartial.Path));
        var query = System.Web.HttpUtility.ParseQueryString(location.Query);
        Assert.Equal("client-id-1", query["client_id"]);
        Assert.Equal("http://localhost/trips/strava/callback/", query["redirect_uri"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("activity:read_all", query["scope"]);
        Assert.False(string.IsNullOrEmpty(query["state"]));
        Assert.NotEqual(query["state"], System.Web.HttpUtility.ParseQueryString(second.Headers.Location!.Query)["state"]);
        Assert.DoesNotContain("client-secret-1", location.OriginalString);

        // Stav je v cookie vázané na prohlížeč, ne v URL: HttpOnly, SameSite=Lax (callback je přesměrování z jiné domény).
        var cookie = Assert.Single(first.Headers.GetValues("Set-Cookie"), c => c.StartsWith("baruchstreks_strava_state=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(query["state"]!, cookie);
    }

    /// <summary>Projde connect a vrátí hodnotu state, kterou by Strava poslala zpět.</summary>
    private static async Task<string> StartConnectAsync(HttpClient client, string? tripId = null)
    {
        var url = tripId is null ? "/trips/strava/connect/" : "/trips/strava/connect/?trip_id=" + tripId;
        var response = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        return System.Web.HttpUtility.ParseQueryString(response.Headers.Location!.Query)["state"]!;
    }

    [Fact]
    public async Task Callback_StoresTokensInConfigPartition()
    {
        await using var factory = Configured(azurite);
        var userName = UniqueName();
        var admin = await factory.CreateUserClientAsync(isAdmin: true, userName);
        factory.Strava().RespondToken(
            "{\"token_type\":\"Bearer\",\"access_token\":\"brand-new-access\",\"refresh_token\":\"brand-new-refresh\","
            + "\"expires_at\":1893456000,\"expires_in\":21600,\"athlete\":{\"id\":42,\"firstname\":\"Baruch\"}}");
        factory.Strava().Respond("/athlete/activities", "[]");
        var state = await StartConnectAsync(admin);

        var res = await admin.GetAsync($"/trips/strava/callback/?code=the-code&state={state}&scope=read,activity:read_all", Ct);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal("/trips/strava/", res.Headers.Location!.OriginalString);
        var form = Assert.Single(factory.Strava().TokenRequests);
        Assert.Equal("the-code", form["code"]);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("client-id-1", form["client_id"]);
        Assert.Equal("client-secret-1", form["client_secret"]);

        // Přesně ten řádek, který čte i Django: partition Config, klíč strava_tokens_{uživatel}, Value = JSON tokenů.
        var row = (await factory.TripsTableClient().GetEntityAsync<Azure.Data.Tables.TableEntity>(
            "Config", "strava_tokens_" + userName, cancellationToken: Ct)).Value;
        using var doc = JsonDocument.Parse(Assert.IsType<string>(row["Value"]));
        Assert.Equal("brand-new-access", doc.RootElement.GetProperty("access_token").GetString());
        Assert.Equal("brand-new-refresh", doc.RootElement.GetProperty("refresh_token").GetString());
        Assert.Equal(1893456000, doc.RootElement.GetProperty("expires_at").GetInt64());
        Assert.Equal("Baruch", doc.RootElement.GetProperty("athlete").GetProperty("firstname").GetString());

        var html = await PageAsync(admin);
        Assert.Contains("Účet Strava byl propojen.", html);
        Assert.Contains("Odpojit Stravu", html);
        // Hodnoty tokenů a secret se na stránce nikdy neobjeví.
        Assert.DoesNotContain("brand-new", html);
        Assert.DoesNotContain("client-secret-1", html);
    }

    [Fact]
    public async Task Callback_KeepsTripIdThroughOAuth()
    {
        await using var factory = Configured(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        factory.Strava().RespondToken(Tokens);
        var state = await StartConnectAsync(admin, "trip_20240101000000");

        var res = await admin.GetAsync($"/trips/strava/callback/?code=c&state={state}", Ct);

        Assert.Equal("/trips/strava/?trip_id=trip_20240101000000", res.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Callback_WrongState_IsRejected_AndStoresNothing()
    {
        await using var factory = Configured(azurite);
        var userName = UniqueName();
        var admin = await factory.CreateUserClientAsync(isAdmin: true, userName);
        await StartConnectAsync(admin);

        var res = await admin.GetAsync("/trips/strava/callback/?code=the-code&state=not-the-state", Ct);

        Assert.Equal("/trips/strava/", res.Headers.Location!.OriginalString);
        Assert.Contains("Neplatný stav OAuth požadavku, zkus propojení znovu.", await PageAsync(admin));
        Assert.Empty(factory.Strava().TokenRequests);
        Assert.Null(await factory.Services.GetRequiredService<IConfigRepository>().GetAsync("strava_tokens_" + userName));
    }

    [Fact]
    public async Task Callback_WithoutStateCookie_IsRejected()
    {
        await using var factory = Configured(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        // Útočník podstrčí odkaz s vlastním kódem; oběť nikdy nezačala propojení, takže nemá cookie se stavem.
        await admin.GetAsync("/trips/strava/callback/?code=attacker-code&state=anything", Ct);

        Assert.Contains("Neplatný stav OAuth požadavku, zkus propojení znovu.", await PageAsync(admin));
        Assert.Empty(factory.Strava().TokenRequests);
    }

    [Fact]
    public async Task Callback_StateIsSingleUse()
    {
        await using var factory = Configured(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        factory.Strava().RespondToken(Tokens);
        var state = await StartConnectAsync(admin);

        await admin.GetAsync($"/trips/strava/callback/?code=c1&state={state}", Ct);
        await admin.GetAsync($"/trips/strava/callback/?code=c2&state={state}", Ct);

        Assert.Single(factory.Strava().TokenRequests);
    }

    [Fact]
    public async Task Callback_StateFromAnotherUser_IsRejected()
    {
        await using var factory = Configured(azurite);
        var first = await factory.CreateUserClientAsync(isAdmin: true);
        var second = await factory.CreateUserClientAsync(isAdmin: true);
        var connect = await first.GetAsync("/trips/strava/connect/", Ct);
        var state = System.Web.HttpUtility.ParseQueryString(connect.Headers.Location!.Query)["state"];
        var cookie = connect.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("baruchstreks_strava_state=", StringComparison.Ordinal)).Split(';')[0];

        // Cookie prvního admina se dostane do prohlížeče druhého (sdílený počítač); stav patří někomu jinému.
        var request = new HttpRequestMessage(HttpMethod.Get, $"/trips/strava/callback/?code=c&state={state}");
        request.Headers.Add("Cookie", cookie);
        var res = await second.SendAsync(request, Ct);

        Assert.Equal("/trips/strava/", res.Headers.Location!.OriginalString);
        Assert.Empty(factory.Strava().TokenRequests);
    }

    [Theory]
    [InlineData("error=access_denied", "Strava odmítla přístup: access_denied")]
    [InlineData("state={state}", "Strava nevrátila autorizační kód.")]
    public async Task Callback_StravaErrorOrMissingCode_ShowsMessage(string query, string message)
    {
        await using var factory = Configured(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        var state = await StartConnectAsync(admin);

        await admin.GetAsync("/trips/strava/callback/?" + query.Replace("{state}", state), Ct);

        Assert.Contains(message, await PageAsync(admin));
        Assert.Empty(factory.Strava().TokenRequests);
    }

    [Fact]
    public async Task Callback_ExchangeFailure_ShowsErrorWithoutBody()
    {
        await using var factory = Configured(azurite);
        var userName = UniqueName();
        var admin = await factory.CreateUserClientAsync(isAdmin: true, userName);
        factory.Strava().FailToken(400, "{\"message\":\"SECRET-BODY\"}");
        var state = await StartConnectAsync(admin);

        await admin.GetAsync($"/trips/strava/callback/?code=bad&state={state}", Ct);

        var html = await PageAsync(admin);
        Assert.Contains("Výměna kódu za token selhala (400).", html);
        Assert.DoesNotContain("SECRET-BODY", html);
        Assert.Null(await factory.Services.GetRequiredService<IConfigRepository>().GetAsync("strava_tokens_" + userName));
    }

    [Fact]
    public async Task Disconnect_DeletesTokens()
    {
        await using var factory = Configured(azurite);
        var (admin, userName) = await ConnectedAdminAsync(factory);
        factory.Strava().Respond("/athlete/activities", "[]");

        var res = await admin.PostFormAsync("/trips/strava/disconnect/", "/trips/strava/", []);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal("/trips/strava/", res.Headers.Location!.OriginalString);
        Assert.Null(await factory.Services.GetRequiredService<IConfigRepository>().GetAsync("strava_tokens_" + userName));
        var html = await PageAsync(admin);
        Assert.Contains("Propojení se Stravou bylo zrušeno.", html);
        Assert.Contains("Propojit se Stravou", html);
    }

    [Fact]
    public async Task Disconnect_Get_IsNotAllowed()
    {
        await using var factory = Configured(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await admin.GetAsync("/trips/strava/disconnect/", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await admin.GetAsync("/trips/strava/import/1/", Ct)).StatusCode);
    }

    // ----- seznam aktivit -----

    [Fact]
    public async Task ListActivities_MapsFields()
    {
        await using var factory = Configured(azurite);
        var (admin, _) = await ConnectedAdminAsync(factory);
        factory.Strava().Respond("/athlete/activities", ActivitiesJson);

        var raw = await admin.GetStringAsync("/trips/strava/", Ct);
        var html = Decoded(raw);

        Assert.Contains("Odpojit Stravu", html);
        Assert.Contains("<td>Sněžka &lt;&amp;&gt;</td>", raw, StringComparison.Ordinal);
        Assert.Contains("<td>30. 6. 2026</td>", html);
        Assert.Contains("<td>Hike</td>", html);
        Assert.Contains("<td class=\"text-end\">12.35 km</td>", html);
        Assert.Contains("<td class=\"text-end\">812 m</td>", html);
        Assert.Contains("action=\"/trips/strava/import/111/\"", html);
        Assert.Contains("Importovat", html);
        // Aktivita bez GPS nemá tlačítko.
        Assert.Contains("<td>Run</td>", html);
        Assert.Contains("bez GPS", html);
        Assert.DoesNotContain("/trips/strava/import/222/", html);
        Assert.Contains("Bez GPS jsou aktivity nahrané ručně nebo z běžeckého pásu", html);
        Assert.DoesNotContain("stored-access-token", html);
        Assert.DoesNotContain("stored-refresh-token", html);

        var call = Assert.Single(factory.Strava().GetRequests);
        Assert.Equal("stored-access-token", call.AccessToken);
        Assert.Equal("1", call.Query["page"]);
        Assert.Equal("30", call.Query["per_page"]);
    }

    [Fact]
    public async Task ListActivities_PagingAndTripId()
    {
        await using var factory = Configured(azurite);
        var (admin, _) = await ConnectedAdminAsync(factory);
        factory.Strava().Respond("/athlete/activities", ActivitiesJson);

        var html = await PageAsync(admin, "/trips/strava/?page=2&trip_id=trip_20240101000000");

        Assert.Equal("2", factory.Strava().GetRequests[0].Query["page"]);
        Assert.Contains("href=\"?page=1&trip_id=trip_20240101000000\"", html);
        Assert.Contains("Novější", html);
        Assert.Contains("Starší", html);
        Assert.Contains("href=\"?page=3&trip_id=trip_20240101000000\"", html);
        Assert.Contains("Připojit", html);
        Assert.DoesNotContain("Importovat", html);
        Assert.Contains("name=\"trip_id\" value=\"trip_20240101000000\"", html);
    }

    [Theory]
    [InlineData("abc", "1")]
    [InlineData("0", "1")]
    [InlineData("-4", "1")]
    [InlineData("7", "7")]
    public async Task ListActivities_PageParameter_IsClamped(string page, string expected)
    {
        await using var factory = Configured(azurite);
        var (admin, _) = await ConnectedAdminAsync(factory);
        factory.Strava().Respond("/athlete/activities", ActivitiesJson);

        var html = await PageAsync(admin, "/trips/strava/?page=" + page);

        Assert.Equal(expected, factory.Strava().GetRequests[0].Query["page"]);
        Assert.Equal(expected != "1", html.Contains("Novější"));
    }

    [Fact]
    public async Task ListActivities_RefreshesStaleToken_AndSaves()
    {
        await using var factory = Configured(azurite);
        var stale = Tokens.Replace("9999999999", "1000");
        var (admin, userName) = await ConnectedAdminAsync(factory, stale);
        factory.Strava()
            .RespondToken("{\"access_token\":\"refreshed-access\",\"refresh_token\":\"refreshed-refresh\",\"expires_at\":9999999999,\"expires_in\":21600}")
            .Respond("/athlete/activities", "[]");

        await PageAsync(admin);

        Assert.Equal("refreshed-access", factory.Strava().GetRequests[0].AccessToken);
        Assert.Equal("stored-refresh-token", factory.Strava().TokenRequests[0]["refresh_token"]);
        var saved = await factory.Services.GetRequiredService<IConfigRepository>().GetAsync("strava_tokens_" + userName);
        Assert.Contains("refreshed-refresh", saved);
        Assert.Contains("\"athlete\"", saved);
    }

    [Fact]
    public async Task ListActivities_CultureCsCz_UsesDotDecimals()
    {
        await using var factory = Configured(azurite).WithCulture("cs-CZ");
        var (admin, _) = await ConnectedAdminAsync(factory);
        factory.Strava().Respond("/athlete/activities", ActivitiesJson);

        var res = await admin.GetAsync("/trips/strava/", Ct);
        var html = Decoded(await res.Content.ReadAsStringAsync(Ct));

        Assert.Equal("1,5", res.Headers.GetValues("X-Test-Decimal").Single());
        Assert.Contains("12.35 km", html);
        Assert.DoesNotContain("12,35", html);
        Assert.Contains("<td>30. 6. 2026</td>", html);
        Assert.Equal("30", factory.Strava().GetRequests[0].Query["per_page"]);
    }

    [Fact]
    public async Task Api401_ReportsReconnect()
    {
        await using var factory = Configured(azurite);
        var (admin, _) = await ConnectedAdminAsync(factory);
        factory.Strava().Fail("/athlete/activities", 401);

        var html = await PageAsync(admin);

        Assert.Contains("Přístup k účtu Strava vypršel, propoj ho prosím znovu.", html);
        Assert.Contains("alert-warning", html);
        // Po 401 se nabídne nové propojení.
        Assert.Contains("Propojit se Stravou", html);
        Assert.DoesNotContain("Odpojit Stravu", html);
    }

    [Fact]
    public async Task Api429_ReportsRateLimit()
    {
        await using var factory = Configured(azurite);
        var (admin, _) = await ConnectedAdminAsync(factory);
        factory.Strava().Fail("/athlete/activities", 429);

        var html = await PageAsync(admin);

        Assert.Contains("Překročen limit požadavků na Stravu, zkus to prosím za chvíli.", html);
        Assert.Contains("alert-danger", html);
        Assert.Contains("Odpojit Stravu", html);
        Assert.Contains("Žádné aktivity k zobrazení.", html);
    }

    [Fact]
    public async Task Api500_DoesNotLeakResponseBody()
    {
        await using var factory = Configured(azurite);
        var (admin, _) = await ConnectedAdminAsync(factory);
        factory.Strava().Fail("/athlete/activities", 500, "{\"message\":\"stored-access-token exploded\"}");

        var html = await PageAsync(admin);

        Assert.Contains("Strava vrátila chybu 500.", html);
        Assert.DoesNotContain("exploded", html);
    }

    // ----- import -----

    [Fact]
    public async Task Import_BuildsGpxAndRunsTrackFlow()
    {
        await using var factory = Configured(azurite);
        var (admin, _) = await ConnectedAdminAsync(factory);
        SetUpImportableActivity(factory);

        var res = await PostImportAsync(admin, 777);

        // Nový výlet: pole z trasy se vyplní, Strava určí název, typ a datum; žádné konflikty, takže do editoru.
        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        var match = Regex.Match(res.Headers.Location!.OriginalString, @"^/trips/trip/(trip_\d{14})/edit/$");
        Assert.True(match.Success, res.Headers.Location.OriginalString);
        var id = match.Groups[1].Value;

        var gpx = await factory.Tracks().GetGpxAsync(id);
        Assert.NotNull(gpx);
        var gpxText = Encoding.UTF8.GetString(gpx);
        Assert.Contains("<name>Ranní túra</name>", gpxText);
        Assert.Equal(4, Regex.Matches(gpxText, "<trkpt ").Count);
        Assert.Contains("<ele>420.0</ele>", gpxText);
        Assert.Contains("<time>2026-06-30T06:01:00Z</time>", gpxText);
        Assert.NotNull(await factory.Tracks().GetGeoJsonAsync(id));

        var parsed = GpxParser.Parse(gpx);
        var trip = (await factory.Trips().GetAsync(id))!;
        Assert.Equal("Ranní túra", trip.Title);
        Assert.Equal("Trail", trip.TripClass);
        Assert.Equal("2026-06-30", trip.TripCompletedOn);
        Assert.Equal(parsed.Stats.MetersAscend, trip.MetersAscend);
        Assert.Equal(parsed.Stats.MetersDescend, trip.MetersDescend);
        Assert.Equal(parsed.Stats.DurationHours, trip.LengthHours);
        Assert.Equal(parsed.Stats.ToJson(), trip.TrackJson);
        using var parking = JsonDocument.Parse(trip.ParkingJson);
        Assert.Equal(50.0, parking.RootElement.GetProperty("Latitude").GetDouble());
        Assert.Equal(14.0, parking.RootElement.GetProperty("Longtitude").GetDouble());
        using var high = JsonDocument.Parse(trip.HighPointJson);
        Assert.Equal(450.0, high.RootElement.GetProperty("Elevation").GetDouble());

        var edit = Decoded(await admin.GetStringAsync(res.Headers.Location.OriginalString, Ct));
        Assert.Contains($"Trasa ze Stravy importována: {DjangoFormat.Float(parsed.Stats.DistanceKm)} km, ↑{parsed.Stats.MetersAscend} m.", edit);

        var calls = factory.Strava().GetRequests.Select(r => r.Path).Where(p => p != "/athlete/activities").ToList();
        Assert.Equal(["/activities/777", "/activities/777/streams"], calls);
    }

    [Fact]
    public async Task Import_UnmappedSportType_LeavesTripClassBlank()
    {
        await using var factory = Configured(azurite);
        var (admin, _) = await ConnectedAdminAsync(factory);
        SetUpImportableActivity(factory, sportType: "Kayaking");

        var res = await PostImportAsync(admin, 777);

        var id = Regex.Match(res.Headers.Location!.OriginalString, @"trip_\d{14}").Value;
        Assert.Equal("none", (await factory.Trips().GetAsync(id))!.TripClass);
    }

    [Theory]
    [InlineData("Hike", "Trail")]
    [InlineData("Walk", "Trail")]
    [InlineData("Snowshoe", "Trail")]
    [InlineData("Run", "Run")]
    [InlineData("TrailRun", "Run")]
    [InlineData("BackcountrySki", "Skialp")]
    [InlineData("NordicSki", "Skialp")]
    [InlineData("AlpineSki", "Slope")]
    [InlineData("Snowboard", "Slope")]
    [InlineData("RockClimbing", "Climb")]
    public void SportType_MapsToTripClass(string sportType, string expected) =>
        Assert.Equal(expected, StravaTripClass.For(sportType));

    [Fact]
    public async Task Import_IntoExistingTrip_FillsBlanksAndAsksAboutConflicts()
    {
        await using var factory = Configured(azurite);
        var (admin, _) = await ConnectedAdminAsync(factory);
        SetUpImportableActivity(factory);
        var id = await factory.Trips().CreateAsync(new TripFormData
        {
            Title = "Moje túra",
            MetersAscend = 10,
            Description = "Zůstane",
        });

        var res = await PostImportAsync(admin, 777, id);

        // Vyplněné metry se liší od GPX, takže potvrzení jako po nahrání souboru.
        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal($"/trips/trip/{id}/track/apply/", res.Headers.Location!.OriginalString);
        var trip = (await factory.Trips().GetAsync(id))!;
        Assert.Equal("Moje túra", trip.Title);
        Assert.Equal("Zůstane", trip.Description);
        Assert.Equal(10, trip.MetersAscend);
        Assert.True(trip.MetersDescend > 0);
        Assert.Equal("2026-06-30", trip.TripCompletedOn);
        Assert.NotEqual("", trip.TrackJson);
        Assert.NotNull(await factory.Tracks().GetGpxAsync(id));

        var apply = Decoded(await admin.GetStringAsync(res.Headers.Location.OriginalString, Ct));
        Assert.Contains("Převýšení nahoru", apply);
        Assert.Contains("10 m", apply);
    }

    [Fact]
    public async Task Import_IntoExistingTrip_WithoutConflicts_GoesToEditor()
    {
        await using var factory = Configured(azurite);
        var (admin, _) = await ConnectedAdminAsync(factory);
        SetUpImportableActivity(factory);
        var id = await factory.Trips().CreateAsync(new TripFormData { Title = "Prázdný" });

        var res = await PostImportAsync(admin, 777, id);

        Assert.Equal($"/trips/trip/{id}/edit/", res.Headers.Location!.OriginalString);
        Assert.True((await factory.Trips().GetAsync(id))!.MetersAscend > 0);
    }

    [Fact]
    public async Task Import_UnknownTrip_ShowsError_AndFetchesNothing()
    {
        await using var factory = Configured(azurite);
        var (admin, _) = await ConnectedAdminAsync(factory);
        SetUpImportableActivity(factory);
        factory.Strava().Respond("/athlete/activities", "[]");

        var res = await PostImportAsync(admin, 777, "does-not-exist");

        Assert.Equal("/trips/strava/", res.Headers.Location!.OriginalString);
        Assert.Contains("Trip nebyl nalezen.", await PageAsync(admin));
        Assert.Equal(0, await factory.BlobCountAsync(factory.TracksContainer));
    }

    [Fact]
    public async Task Import_NoGps_ShowsError()
    {
        await using var factory = Configured(azurite);
        var (admin, _) = await ConnectedAdminAsync(factory);
        factory.Strava()
            .Respond("/activities/9", """{"id": 9, "name": "Pás", "sport_type": "Run", "start_date_local": "2026-06-30T06:00:00Z", "start_latlng": []}""")
            .Respond("/activities/9/streams", """{"time": {"data": [0, 1]}}""")
            .Respond("/athlete/activities", "[]");
        var before = (await factory.Trips().ListAsync()).Count;

        var res = await PostImportAsync(admin, 9);

        Assert.Equal("/trips/strava/", res.Headers.Location!.OriginalString);
        Assert.Contains("Aktivita neobsahuje GPS data, nelze z ní vytvořit trasu.", await PageAsync(admin));
        Assert.Equal(before, (await factory.Trips().ListAsync()).Count);
        Assert.Equal(0, await factory.BlobCountAsync(factory.TracksContainer));
    }

    [Fact]
    public async Task Import_ApiErrors_AreShownOnActivitiesPage()
    {
        await using var factory = Configured(azurite);
        var (admin, _) = await ConnectedAdminAsync(factory);
        factory.Strava().Fail("/activities/5", 429).Respond("/athlete/activities", "[]");

        var res = await PostImportAsync(admin, 5);

        Assert.Equal("/trips/strava/", res.Headers.Location!.OriginalString);
        Assert.Contains("Překročen limit požadavků na Stravu, zkus to prosím za chvíli.", await PageAsync(admin));
    }

    [Fact]
    public async Task Import_NotConnected_ShowsError()
    {
        await using var factory = Configured(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        var res = await PostImportAsync(admin, 5);

        Assert.Equal("/trips/strava/", res.Headers.Location!.OriginalString);
        Assert.Contains("Účet Strava není propojený.", await PageAsync(admin));
        Assert.Empty(factory.Strava().GetRequests);
    }

    [Fact]
    public async Task Import_NotConfigured_ShowsError()
    {
        await using var factory = new AppFactory(azurite);
        // Zastaralé tokeny z doby, kdy Strava ještě byla nastavená: obnova by potřebovala secret.
        var (admin, _) = await ConnectedAdminAsync(factory, Tokens.Replace("9999999999", "1000"));

        var res = await PostImportAsync(admin, 5);

        Assert.Equal("/trips/strava/", res.Headers.Location!.OriginalString);
        Assert.Empty(factory.Strava().TokenRequests);
        Assert.Contains("Strava není nakonfigurovaná - nastav STRAVA_CLIENT_ID a STRAVA_CLIENT_SECRET.", await PageAsync(admin));
    }

    [Fact]
    public async Task Import_NonNumericActivityId_Is404()
    {
        await using var factory = Configured(azurite);
        var (admin, _) = await ConnectedAdminAsync(factory);
        factory.Strava().Respond("/athlete/activities", "[]");

        var res = await admin.PostFormAsync("/trips/strava/import/abc/", "/trips/strava/", []);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.DoesNotContain(factory.Strava().GetRequests, r => r.Path.StartsWith("/activities/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Import_CreateFailure_InProduction_DoesNotLeakExceptionDetails()
    {
        await using var factory = Configured(azurite).WithEnvironment("Production")
            .ConfigureTestServices(services => services.AddSingleton<ITripRepository, ThrowingCreateRepository>());
        var userName = UniqueName();
        await factory.CreateUserAsync(userName, AppFactory.TestPassword, isAdmin: true);
        var admin = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
        Assert.Equal(HttpStatusCode.Found, (await factory.LoginAsync(admin, userName, AppFactory.TestPassword)).StatusCode);
        await factory.Services.GetRequiredService<IConfigRepository>().SaveAsync("strava_tokens_" + userName, Tokens);
        SetUpImportableActivity(factory);
        factory.Strava().Respond("/athlete/activities", "[]");

        var res = await PostImportAsync(admin, 777);
        var html = Decoded(await admin.GetStringAsync("/trips/strava/", Ct));

        Assert.Equal("/trips/strava/", res.Headers.Location!.OriginalString);
        Assert.Contains("Vytvoření tripu selhalo.", html);
        Assert.DoesNotContain("SECRET-DETAIL", html);
    }

    private sealed class ThrowingCreateRepository : ITripRepository
    {
        public Task<IReadOnlyList<Trip>> ListAsync() => Task.FromResult<IReadOnlyList<Trip>>([]);
        public Task<Trip?> GetAsync(string id) => Task.FromResult<Trip?>(null);
        public Task<string> CreateAsync(TripFormData data) => throw new InvalidOperationException("SECRET-DETAIL table exploded");
        public Task UpdateAsync(string id, TripFormData data) => throw new InvalidOperationException("SECRET-DETAIL table exploded");
        public Task MergeFieldsAsync(string id, IReadOnlyDictionary<string, object?> fields) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
    }

    // ----- oprávnění a izolace od sítě -----

    [Theory]
    [InlineData("GET", "/trips/strava/")]
    [InlineData("GET", "/trips/strava/connect/")]
    [InlineData("GET", "/trips/strava/callback/?code=c&state=s")]
    [InlineData("POST", "/trips/strava/disconnect/")]
    [InlineData("POST", "/trips/strava/import/123/")]
    public async Task Pages_RequireAdmin(string method, string url)
    {
        await using var factory = Configured(azurite);
        var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var plainUser = await factory.CreateUserClientAsync(isAdmin: false);

        foreach (var client in new[] { anonymous, plainUser })
        {
            var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (method == "POST")
                request.Content = new FormUrlEncodedContent([]);
            var res = await client.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.Found, res.StatusCode);
            Assert.StartsWith("/accounts/login/?next=", res.Headers.Location!.OriginalString);
        }

        Assert.Empty(factory.Strava().TokenRequests);
        Assert.Empty(factory.Strava().GetRequests);
    }

    [Fact]
    public async Task TestHost_NeverUsesRealStravaClient()
    {
        await using var factory = new AppFactory(azurite);
        _ = factory.CreateClient();

        var client = factory.Services.GetRequiredService<IStravaClient>();

        Assert.IsType<FakeStravaClient>(client);
        Assert.IsNotType<StravaClient>(client);
        Assert.Same(factory.Strava(), client);
    }

    [Fact]
    public async Task StravaClient_IsTheDefaultImplementation_WithPythonTimeout()
    {
        // Bez testovací náhrady aplikace používá skutečný klient s timeoutem jako Python (30 s). Jen se sestaví, nic se nevolá.
        var services = new ServiceCollection();
        services.AddStravaClient();
        await using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IStravaClient>();

        Assert.IsType<StravaClient>(client);
        Assert.Equal(TimeSpan.FromSeconds(30), StravaClient.RequestTimeout);
    }
}
