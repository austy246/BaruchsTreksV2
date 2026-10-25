using System.Net;
using System.Text.RegularExpressions;
using Azure.Data.Tables;
using BaruchsTreks.Photos;
using BaruchsTreks.Storage;
using BaruchsTreks.Trips;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BaruchsTreks.Tests.Integration;

public partial class TripEditTests(AzuriteFixture azurite)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebApplicationFactoryClientOptions NoRedirect => new() { AllowAutoRedirect = false };

    private const string ParkingJson = """{"Latitude": 49.1, "Longtitude": 20.2}""";
    private const string HighPointJson = """{"Latitude": 49.2, "Longtitude": 20.1}""";

    private static (string, string)[] ValidFields() =>
    [
        ("title", "Sněžka"),
        ("description", "Hezký výlet"),
        ("trip_completed_on", "2025-08-15"),
        ("length_hours", "6.5"),
        ("participants", "Já a Baruch"),
        ("meters_ascend", "1200"),
        ("meters_descend", "1100"),
        ("uiaa_grade", "IV"),
        ("alpine_grade", "PD"),
        ("trip_class", "Trail"),
        ("ferata_grade", "B"),
        ("parking_json", ParkingJson),
        ("high_point_json", HighPointJson),
    ];

    private static string Decoded(string html) => WebUtility.HtmlDecode(html);

    [GeneratedRegex(@"^/trips/trip/(trip_\d{14})/$")]
    private static partial Regex DetailLocation();

    public static TheoryData<string> AdminUrls => new()
    {
        "/trips/trip/new/",
        "/trips/trip/trip_20250101000000/edit/",
        "/trips/trip/trip_20250101000000/copy/",
        "/trips/trip/trip_20250101000000/delete/",
        "/trips/trip/trip_20250101000000/track/apply/",
    };

    [Theory]
    [MemberData(nameof(AdminUrls))]
    public async Task Create_Anonymous_RedirectsToLogin(string url)
    {
        await using var factory = new AppFactory(azurite);

        var res = await factory.CreateClient(NoRedirect).GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal("/accounts/login/?next=" + Uri.EscapeDataString(url), res.Headers.Location?.OriginalString);
    }

    [Theory]
    [MemberData(nameof(AdminUrls))]
    public async Task AdminPages_NonAdmin_RedirectToLogin(string url)
    {
        await using var factory = new AppFactory(azurite);
        var client = await factory.CreateUserClientAsync(isAdmin: false);

        var res = await client.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal("/accounts/login/?next=" + Uri.EscapeDataString(url), res.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("/trips/trip/new/")]
    [InlineData("/trips/trip/trip_20250101000000/edit/")]
    [InlineData("/trips/trip/trip_20250101000000/delete/")]
    [InlineData("/trips/trip/trip_20250101000000/track/apply/")]
    [InlineData("/trips/trip/trip_20250101000000/track/delete/")]
    public async Task Posts_Anonymous_RedirectToLoginWithoutTouchingData(string url)
    {
        await using var factory = new AppFactory(azurite);
        var client = factory.CreateClient(NoRedirect);

        // Bez antiforgery tokenu: přihlášení se kontroluje dřív než token.
        var res = await client.PostAsync(url, new FormUrlEncodedContent([new("title", "x")]), Ct);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.StartsWith("/accounts/login/?next=", res.Headers.Location?.OriginalString);
        Assert.Empty(await factory.Trips().ListAsync());
    }

    [Fact]
    public async Task Create_ShowsEmptyFormWithDjangoTexts()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        var html = await admin.GetStringAsync("/trips/trip/new/", Ct);

        Assert.Contains("<h1>Create New Trip</h1>", html);
        Assert.Contains("Back to Trips", html);
        Assert.Contains("Trip Information", html);
        Assert.Contains("<title>Create New Trip - Baruch's Treks</title>", Decoded(html));
        Assert.Contains("name=\"title\"", html);
        Assert.Contains("maxlength=\"200\"", html);
        Assert.Contains("<option value=\"Mountaineering\">VHT</option>", html);
        Assert.Contains("<option value=\"\">--Vyber kategorii--</option>", html);
        Assert.Contains("<option value=\"ED5\">ED5 (Extrêmement Difficile 5/Extremely Difficult 5)</option>", html);
        Assert.Contains("name=\"gpx_file\"", html);
        Assert.Contains("accept=\".gpx,application/gpx+xml\"", html);
        Assert.Contains("name=\"photos\" multiple", html);
        Assert.Contains("name=\"__RequestVerificationToken\"", html);
        Assert.Contains("enctype=\"multipart/form-data\"", html);
        Assert.Contains("Create Trip", html);
        Assert.Contains("const API_KEY = 'test-key';", html);
        Assert.DoesNotContain("trackDeleteForm", html);
    }

    [Fact]
    public async Task Create_ValidForm_WritesEntityWithDjangoTypes()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        var res = await admin.PostFormAsync("/trips/trip/new/", "/trips/trip/new/", ValidFields());

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        var match = DetailLocation().Match(res.Headers.Location!.OriginalString);
        Assert.True(match.Success, res.Headers.Location.OriginalString);
        var raw = await factory.RawTripAsync(match.Groups[1].Value);
        Assert.Equal("Trips", raw.PartitionKey);
        Assert.Equal("Sněžka", Assert.IsType<string>(raw["Title"]));
        Assert.Equal("Hezký výlet", Assert.IsType<string>(raw["Description"]));
        Assert.Equal("2025-08-15", Assert.IsType<string>(raw["TripCompletedOn"]));
        Assert.Equal(6.5, Assert.IsType<double>(raw["LengthHours"]));
        Assert.Equal(1200, Assert.IsType<int>(raw["MetersAscend"]));
        Assert.Equal(1100, Assert.IsType<int>(raw["MetersDescend"]));
        Assert.Equal("Já a Baruch", Assert.IsType<string>(raw["Participants"]));
        Assert.Equal("IV", Assert.IsType<string>(raw["UiaaGrade"]));
        Assert.Equal("PD", Assert.IsType<string>(raw["AlpineGrade"]));
        Assert.Equal("Trail", Assert.IsType<string>(raw["TripClass"]));
        Assert.Equal("B", Assert.IsType<string>(raw["FerataGrade"]));
        Assert.Equal(ParkingJson, Assert.IsType<string>(raw["ParkingJson"]));
        Assert.Equal(HighPointJson, Assert.IsType<string>(raw["HighPointJson"]));
        Assert.Equal("", Assert.IsType<string>(raw["TrackJson"]));
        Assert.False(raw.ContainsKey("ElevationGain"));

        // Detail nového výletu se vykreslí.
        var detail = await admin.GetAsync(res.Headers.Location.OriginalString, Ct);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
    }

    [Fact]
    public async Task Create_MinimalForm_StoresBlanksAndNoNumbers()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        var res = await admin.PostFormAsync("/trips/trip/new/", "/trips/trip/new/",
            [("title", "  Jen název  "), ("length_hours", ""), ("meters_ascend", ""), ("trip_completed_on", "")]);

        var id = DetailLocation().Match(res.Headers.Location!.OriginalString).Groups[1].Value;
        var raw = await factory.RawTripAsync(id);
        Assert.Equal("Jen název", raw["Title"]);
        Assert.Equal("", raw["Description"]);
        Assert.False(raw.ContainsKey("LengthHours"));
        Assert.False(raw.ContainsKey("MetersAscend"));
        Assert.False(raw.ContainsKey("TripCompletedOn"));
    }

    [Fact]
    public async Task Create_InvalidForm_ShowsErrorsAndSavesNothing()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        var res = await admin.PostFormAsync("/trips/trip/new/", "/trips/trip/new/",
        [
            ("title", ""),
            ("trip_completed_on", "tomorrow"),
            ("length_hours", "-1"),
            ("meters_ascend", "1.5"),
            ("meters_descend", "abc"),
            ("trip_class", "Swimming"),
            ("uiaa_grade", "XIII"),
            ("description", "Zůstane ve formuláři"),
        ]);
        var html = Decoded(await res.Content.ReadAsStringAsync(Ct));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("<ul class=\"errorlist\"><li>This field is required.</li></ul>", html);
        Assert.Contains("<li>Enter a valid date.</li>", html);
        Assert.Contains("<li>Ensure this value is greater than or equal to 0.</li>", html);
        Assert.Contains("<li>Enter a whole number.</li>", html);
        Assert.Contains("<li>Select a valid choice. Swimming is not one of the available choices.</li>", html);
        Assert.Contains("<li>Select a valid choice. XIII is not one of the available choices.</li>", html);
        Assert.Contains("Zůstane ve formuláři", html);
        Assert.Empty(await factory.Trips().ListAsync());
    }

    [Fact]
    public async Task Create_TitleOver200Chars_IsRejected()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        var res = await admin.PostFormAsync("/trips/trip/new/", "/trips/trip/new/", [("title", new string('x', 201))]);
        var html = await res.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Ensure this value has at most 200 characters (it has 201).", html);
        Assert.Empty(await factory.Trips().ListAsync());
    }

    [Fact]
    public async Task Post_WithoutAntiforgeryToken_IsRejected()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        var res = await admin.PostAsync("/trips/trip/new/", new FormUrlEncodedContent([new("title", "x")]), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(await factory.Trips().ListAsync());
    }

    [Fact]
    public async Task Edit_ShowsExistingValues()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        await factory.PutRawTripAsync(LegacyEntities.A("trip_20250101000000"));
        await factory.Services.GetRequiredService<IPhotoStore>()
            .UploadAsync("trip_20250101000000", new MemoryStream(TestImages.Png(4, 4)), "a.png");

        var html = await admin.GetStringAsync("/trips/trip/trip_20250101000000/edit/", Ct);
        var text = Decoded(html);

        Assert.Contains("<h1>Edit Trip: Legacy A</h1>", text);
        Assert.Contains("Back to Trip", text);
        Assert.Contains("Edit Trip Information", text);
        Assert.Contains("value=\"Legacy A\"", text);
        Assert.Contains("value=\"2025-08-15\"", text);
        Assert.Contains("name=\"length_hours\"", text);
        Assert.Matches("name=\"length_hours\"[^>]*value=\"6\"", text);
        Assert.Matches("name=\"meters_ascend\"[^>]*value=\"1200\"", text);
        Assert.Contains("<option value=\"PD\" selected>", text);
        Assert.Contains("<option value=\"none\" selected>none</option>", text);
        Assert.Contains($"name=\"parking_json\" value=\"{ParkingJson}\"", text);
        Assert.Contains("Existing Photos", text);
        Assert.Contains("Save Changes", text);
        Assert.Contains("Starý záznam", text);
        Assert.DoesNotContain("trackDeleteForm", text);
        Assert.DoesNotContain("Stáhnout GPX", text);
    }

    [Fact]
    public async Task Edit_PreservesUnpostedProperties()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        await factory.PutRawTripAsync(LegacyEntities.A("trip_20250101000000"));

        var res = await admin.PostFormAsync("/trips/trip/trip_20250101000000/edit/", "/trips/trip/trip_20250101000000/edit/",
        [
            ("title", "Přejmenováno"),
            ("description", "Nový popis"),
            ("length_hours", "7"),
            ("meters_ascend", "1300"),
            ("alpine_grade", "AD"),
        ]);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal("/trips/trip/trip_20250101000000/", res.Headers.Location?.OriginalString);
        var raw = await factory.RawTripAsync("trip_20250101000000");
        Assert.Equal("Přejmenováno", raw["Title"]);
        Assert.Equal("Nový popis", raw["Description"]);
        Assert.Equal("AD", raw["AlpineGrade"]);
        // Číselné vlastnosti si drží svůj typ (Int32 zůstává Int32).
        Assert.Equal(7, Assert.IsType<int>(raw["LengthHours"]));
        Assert.Equal(1300, Assert.IsType<int>(raw["MetersAscend"]));
        // Nic z toho formulář neposílá.
        Assert.Equal(1200, Assert.IsType<int>(raw["ElevationGain"]));
        Assert.Equal("Tatry", raw["Location"]);
        Assert.Equal("https://mapy.example/a", raw["MapUrl"]);
        Assert.Equal("https://img.example/a.jpg", raw["ImageUrl"]);
        Assert.Equal("zůstane", raw["Extra"]);
        // Prázdná hodnota data a JSON polí nic nemaže.
        Assert.Equal("2025-08-15", Assert.IsType<string>(raw["TripCompletedOn"]));
        Assert.Equal("""{"Latitude": 49.1, "Longtitude": 20.2}""", raw["ParkingJson"]);
        Assert.Equal("""{"Latitude": 49.2, "Longtitude": 20.1}""", raw["HighPointJson"]);
    }

    [Fact]
    public async Task Edit_OnTripWithDateTimeCompletion_KeepsDateTimeType()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        await factory.PutRawTripAsync(LegacyEntities.B("trip_20250202000000"));

        var res = await admin.PostFormAsync("/trips/trip/trip_20250202000000/edit/", "/trips/trip/trip_20250202000000/edit/",
            [("title", "B"), ("trip_completed_on", "2024-07-02"), ("length_hours", "5")]);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        var raw = await factory.RawTripAsync("trip_20250202000000");
        Assert.Equal(new DateTimeOffset(2024, 7, 2, 0, 0, 0, TimeSpan.Zero), Assert.IsType<DateTimeOffset>(raw["TripCompletedOn"]));
        Assert.Equal(5.0, Assert.IsType<double>(raw["LengthHours"]));
        Assert.Equal(810.5, raw["MetersAscend"]);
        Assert.Equal("""{"distance_km": 12.3}""", raw["TrackJson"]);
    }

    [Theory]
    [InlineData("does-not-exist")]
    [InlineData("a'b")]
    public async Task Edit_UnknownId_Returns404(string id)
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        var url = $"/trips/trip/{Uri.EscapeDataString(id)}/edit/";

        var get = await admin.GetAsync(url, Ct);
        var post = await admin.PostFormAsync(url, "/trips/trip/new/", [("title", "x")]);

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Empty(await factory.Trips().ListAsync());
    }

    [Fact]
    public async Task Edit_InvalidForm_KeepsEntityAndShowsErrors()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        await factory.PutRawTripAsync(LegacyEntities.A("trip_20250101000000"));

        var res = await admin.PostFormAsync("/trips/trip/trip_20250101000000/edit/", "/trips/trip/new/",
            [("title", ""), ("description", "Změna")]);
        var html = await res.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("This field is required.", html);
        Assert.Contains("<h1>Edit Trip: Legacy A</h1>", html);
        Assert.Equal("Starý záznam", (await factory.RawTripAsync("trip_20250101000000"))["Description"]);
    }

    [Fact]
    public async Task Edit_UnderCzechCulture_UsesDotsAndParsesDots()
    {
        await using var factory = new AppFactory(azurite).WithCulture("cs-CZ");
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        await factory.PutRawTripAsync(LegacyEntities.B("trip_20250202000000"));

        var html = await admin.GetStringAsync("/trips/trip/trip_20250202000000/edit/", Ct);
        Assert.Matches("name=\"length_hours\"[^>]*value=\"4\\.5\"", html);

        var res = await admin.PostFormAsync("/trips/trip/trip_20250202000000/edit/", "/trips/trip/new/",
            [("title", "B"), ("length_hours", "5.5"), ("meters_ascend", "900")]);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal(5.5, (await factory.RawTripAsync("trip_20250202000000"))["LengthHours"]);
    }

    [Fact]
    public async Task Copy_AppendsCopyAndSkipsPhotos()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        var original = await factory.Trips().CreateAsync(new TripFormData
        {
            Title = "Okruh",
            Description = "Popis",
            TripCompletedOn = new DateOnly(2025, 5, 1),
            LengthHours = 3.5,
            MetersAscend = 700,
            UiaaGrade = "V",
            ParkingJson = ParkingJson,
            HighPointJson = HighPointJson,
            Location = "Krkonoše",
        });
        await factory.Services.GetRequiredService<IPhotoStore>()
            .UploadAsync(original, new MemoryStream(TestImages.Png(4, 4)), "a.png");

        var res = await admin.GetAsync($"/trips/trip/{original}/copy/", Ct);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        var match = Regex.Match(res.Headers.Location!.OriginalString, @"^/trips/trip/(trip_\d{14})/edit/$");
        Assert.True(match.Success, res.Headers.Location.OriginalString);
        var copyId = match.Groups[1].Value;
        Assert.NotEqual(original, copyId);

        var copy = (await factory.Trips().GetAsync(copyId))!;
        Assert.Equal("Okruh (copy)", copy.Title);
        Assert.Equal("Popis", copy.Description);
        Assert.Equal("2025-05-01", copy.TripCompletedOn);
        Assert.Equal(3.5, copy.LengthHours);
        Assert.Equal(700, copy.MetersAscend);
        Assert.Equal("V", copy.UiaaGrade);
        Assert.Equal(ParkingJson, copy.ParkingJson);
        Assert.Equal(HighPointJson, copy.HighPointJson);
        Assert.Equal("Krkonoše", copy.Location);

        var photos = factory.Services.GetRequiredService<IPhotoStore>();
        Assert.Empty(await photos.ListAsync(copyId));
        Assert.Single(await photos.ListAsync(original));
        Assert.Equal("Okruh", (await factory.Trips().GetAsync(original))!.Title);
    }

    [Fact]
    public async Task Copy_CopiesTrackBlobsSoStatsAndFilesStayConsistent()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        var gpx = AppFactoryExtensions.GpxFixture("stats.gpx");
        var parsed = BaruchsTreks.Tracks.GpxParser.Parse(gpx);
        var original = await factory.Trips().CreateAsync(new TripFormData { Title = "S trasou", TrackJson = parsed.Stats.ToJson() });
        await factory.Tracks().SaveAsync(original, gpx, System.Text.Encoding.UTF8.GetBytes(parsed.GeoJson));

        var res = await admin.GetAsync($"/trips/trip/{original}/copy/", Ct);

        var copyId = Regex.Match(res.Headers.Location!.OriginalString, @"/trips/trip/(trip_\d{14})/edit/").Groups[1].Value;
        Assert.Equal(parsed.Stats.ToJson(), (await factory.Trips().GetAsync(copyId))!.TrackJson);
        Assert.Equal(gpx, await factory.Tracks().GetGpxAsync(copyId));
        Assert.NotNull(await factory.Tracks().GetGeoJsonAsync(copyId));
        Assert.Equal(gpx, await factory.Tracks().GetGpxAsync(original));
    }

    [Theory]
    [InlineData("does-not-exist")]
    [InlineData("a'b")]
    public async Task Copy_UnknownId_Returns404(string id)
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        var res = await admin.GetAsync($"/trips/trip/{Uri.EscapeDataString(id)}/copy/", Ct);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Empty(await factory.Trips().ListAsync());
    }

    [Fact]
    public async Task Delete_Get_ShowsConfirmation()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        var id = await factory.Trips().CreateAsync(new TripFormData { Title = "Mazaný" });

        var res = await admin.GetAsync($"/trips/trip/{id}/delete/", Ct);
        var html = await res.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("<title>Delete Trip</title>", html);
        Assert.Contains("Confirm Delete", html);
        Assert.Contains("Are you sure you want to delete this trip? This action cannot be undone.", html);
        Assert.Contains("Yes, Delete", html);
        Assert.Contains($"href=\"/trips/trip/{id}/\" class=\"btn btn-secondary ms-2\">Cancel</a>", html);
        Assert.Contains("name=\"__RequestVerificationToken\"", html);
        Assert.NotNull(await factory.Trips().GetAsync(id));
    }

    [Fact]
    public async Task Delete_RemovesTrip()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        var id = await factory.Trips().CreateAsync(new TripFormData { Title = "Mazaný" });
        var other = await factory.Trips().CreateAsync(new TripFormData { Title = "Zůstane" });
        await factory.Tracks().SaveAsync(id, [1], [2]);
        await factory.Tracks().SaveAsync(other, [3], [4]);

        var res = await admin.PostFormAsync($"/trips/trip/{id}/delete/", $"/trips/trip/{id}/delete/", []);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal("/trips/all/", res.Headers.Location?.OriginalString);
        Assert.Null(await factory.Trips().GetAsync(id));
        Assert.Null(await factory.Tracks().GetGpxAsync(id));
        Assert.Null(await factory.Tracks().GetGeoJsonAsync(id));
        Assert.NotNull(await factory.Trips().GetAsync(other));
        Assert.NotNull(await factory.Tracks().GetGpxAsync(other));
        var list = await admin.GetStringAsync("/trips/all/", Ct);
        Assert.Contains("Trip deleted successfully.", list);
        Assert.DoesNotContain("Mazaný", list);
    }

    [Fact]
    public async Task Delete_UnknownTrip_FlashesErrorAndRedirects()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        var res = await admin.PostFormAsync("/trips/trip/nope/delete/", "/trips/trip/nope/delete/", []);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal("/trips/all/", res.Headers.Location?.OriginalString);
        Assert.Contains("Failed to delete trip", await admin.GetStringAsync("/trips/all/", Ct));
    }

    [Fact]
    public async Task Errors_InProduction_DoNotLeakExceptionDetails()
    {
        await using var factory = new AppFactory(azurite).WithEnvironment("Production").ConfigureTestServices(ThrowingRepository);
        var admin = await SecureClientAsync(factory);

        var res = await admin.PostFormAsync("/trips/trip/new/", "/trips/trip/new/", [("title", "x")]);
        var html = await res.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Error creating trip", html);
        Assert.DoesNotContain("SECRET-DETAIL", html);
    }

    [Fact]
    public async Task Errors_InDevelopment_ShowExceptionDetails()
    {
        await using var factory = new AppFactory(azurite).ConfigureTestServices(ThrowingRepository);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        var res = await admin.PostFormAsync("/trips/trip/new/", "/trips/trip/new/", [("title", "x")]);

        Assert.Contains("SECRET-DETAIL", await res.Content.ReadAsStringAsync(Ct));
    }

    private static void ThrowingRepository(IServiceCollection services) =>
        services.AddSingleton<ITripRepository>(sp => new FailingCreateRepository(new TripRepository(
            sp.GetRequiredService<TableServiceClient>(), sp.GetRequiredService<IOptions<StorageOptions>>())));

    /// <summary>V Production je cookie Secure, takže klient musí mluvit https.</summary>
    internal static async Task<HttpClient> SecureClientAsync(AppFactory factory)
    {
        var userName = "admin-" + Guid.NewGuid().ToString("N")[..8];
        await factory.CreateUserAsync(userName, AppFactory.TestPassword, isAdmin: true);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
        var login = await factory.LoginAsync(client, userName, AppFactory.TestPassword);
        Assert.Equal(HttpStatusCode.Found, login.StatusCode);
        return client;
    }

    private sealed class FailingCreateRepository(ITripRepository inner) : ITripRepository
    {
        public Task<IReadOnlyList<Trip>> ListAsync() => inner.ListAsync();
        public Task<Trip?> GetAsync(string id) => inner.GetAsync(id);
        public Task<string> CreateAsync(TripFormData data) => throw new InvalidOperationException("SECRET-DETAIL table exploded");
        public Task UpdateAsync(string id, TripFormData data) => throw new InvalidOperationException("SECRET-DETAIL table exploded");
        public Task MergeFieldsAsync(string id, IReadOnlyDictionary<string, object?> fields) => inner.MergeFieldsAsync(id, fields);
        public Task DeleteAsync(string id) => inner.DeleteAsync(id);
    }
}
