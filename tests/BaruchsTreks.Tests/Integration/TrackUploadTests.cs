using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BaruchsTreks.Photos;
using BaruchsTreks.Tracks;
using BaruchsTreks.Trips;
using Microsoft.Extensions.DependencyInjection;

namespace BaruchsTreks.Tests.Integration;

/// <summary>Nahrání GPX a fotek při vytvoření a úpravě výletu, potvrzení hodnot z GPX, smazání trasy.</summary>
public partial class TrackUploadTests(AzuriteFixture azurite)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string NewUrl = "/trips/trip/new/";

    private static byte[] StatsGpx => AppFactoryExtensions.GpxFixture("stats.gpx");

    private static UploadFile Gpx(byte[] content, string name = "track.gpx") => new("gpx_file", name, content, "application/gpx+xml");

    private static UploadFile Photo(byte[] content, string name) => new("photos", name, content, "image/jpeg");

    private static string Decoded(string html) => WebUtility.HtmlDecode(html);

    [GeneratedRegex(@"/trips/trip/(trip_\d{14})/")]
    private static partial Regex TripIdIn();

    private static string IdFrom(HttpResponseMessage response) =>
        TripIdIn().Match(response.Headers.Location!.OriginalString).Groups[1].Value;

    private static string EditUrl(string id) => $"/trips/trip/{id}/edit/";

    [Fact]
    public async Task Upload_Gpx_FillsBlankFieldsAndStoresTrack()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        var res = await admin.PostFormAsync(NewUrl, NewUrl, [("title", "S GPX")], Gpx(StatsGpx));

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        var id = IdFrom(res);
        // Bylo co předvyplnit a nic nekoliduje, takže zpět do editoru.
        Assert.Equal(EditUrl(id), res.Headers.Location!.OriginalString);

        var raw = await factory.RawTripAsync(id);
        Assert.Equal(300, Assert.IsType<int>(raw["MetersAscend"]));
        Assert.Equal(250, Assert.IsType<int>(raw["MetersDescend"]));
        Assert.Equal(0.5, Assert.IsType<double>(raw["LengthHours"]));
        Assert.Equal("2025-07-12", Assert.IsType<string>(raw["TripCompletedOn"]));
        using var parking = JsonDocument.Parse(Assert.IsType<string>(raw["ParkingJson"]));
        Assert.Equal(50.0, parking.RootElement.GetProperty("Latitude").GetDouble());
        Assert.Equal(14.0, parking.RootElement.GetProperty("Longtitude").GetDouble());
        using var high = JsonDocument.Parse(Assert.IsType<string>(raw["HighPointJson"]));
        Assert.Equal(700.0, high.RootElement.GetProperty("Elevation").GetDouble());
        var stats = StoredTrackStats.Read(Assert.IsType<string>(raw["TrackJson"]));
        Assert.Equal(0.33, stats!.DistanceKm);

        Assert.Equal(StatsGpx, await factory.Tracks().GetGpxAsync(id));
        Assert.Contains("\"Feature\"", Encoding.UTF8.GetString((await factory.Tracks().GetGeoJsonAsync(id))!));

        var edit = Decoded(await admin.GetStringAsync(res.Headers.Location.OriginalString, Ct));
        Assert.Contains("Trasa nahrána: 0.33 km, ↑300 m.", edit);
        Assert.Contains("Prázdná pole jsou předvyplněná z GPX. Můžeš je upravit (vrchol přetažením značky na mapě) a uložit znovu.", edit);
        Assert.Contains("value=\"300\"", edit);
        Assert.Contains("0.33 km", edit);
        Assert.Contains("trackDeleteForm", edit);
        Assert.Contains($"/trips/trip/{id}/track.gpx", edit);
        Assert.Contains($"fetch('/trips/trip/{id}/track.geojson')", edit);
        Assert.Contains("Nahradit trasu", edit);
    }

    [Fact]
    public async Task Upload_Gpx_SameValues_GoesToDetail()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        var stats = GpxParser.Parse(StatsGpx).Stats;
        // Formulář už obsahuje přesně to, co GPX dodá: nic se nevyplňuje a nic nekoliduje.
        var posted = new List<(string, string)>
        {
            ("title", "Hotovo"),
            ("meters_ascend", "300"),
            ("meters_descend", "250"),
            ("length_hours", "0.5"),
            ("trip_completed_on", "2025-07-12"),
            ("parking_json", """{"Latitude": 50.0, "Longtitude": 14.0}"""),
            ("high_point_json", """{"Latitude": 50.002, "Longtitude": 14.0, "Elevation": 700.0}"""),
        };
        Assert.Equal(300, stats.MetersAscend);

        var res = await admin.PostFormAsync(NewUrl, NewUrl, posted, Gpx(StatsGpx));

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Matches(@"^/trips/trip/trip_\d{14}/$", res.Headers.Location!.OriginalString);
        Assert.NotNull(await factory.Tracks().GetGpxAsync(IdFrom(res)));
        Assert.NotNull(StoredTrackStats.Read((await factory.Trips().GetAsync(IdFrom(res)))!.TrackJson));
    }

    [Fact]
    public async Task Upload_Gpx_DifferentValues_GoesToApplyPage()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        var id = await factory.Trips().CreateAsync(new TripFormData { Title = "Má hodnoty", MetersAscend = 100, LengthHours = 2.0 });

        var res = await admin.PostFormAsync(EditUrl(id), EditUrl(id),
            [("title", "Má hodnoty"), ("meters_ascend", "100"), ("length_hours", "2.0")], Gpx(StatsGpx));

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal($"/trips/trip/{id}/track/apply/", res.Headers.Location!.OriginalString);
        var raw = await factory.RawTripAsync(id);
        // Konflikty se nepřepsaly, prázdná pole se vyplnila, trasa i statistiky jsou uložené.
        Assert.Equal(100, raw["MetersAscend"]);
        Assert.Equal(2.0, raw["LengthHours"]);
        Assert.Equal(250, raw["MetersDescend"]);
        Assert.Equal("2025-07-12", raw["TripCompletedOn"]);
        Assert.NotNull(await factory.Tracks().GetGpxAsync(id));
        Assert.NotNull(StoredTrackStats.Read((string)raw["TrackJson"]));

        var page = await admin.GetAsync(res.Headers.Location.OriginalString, Ct);
        var html = Decoded(await page.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("<title>Přepsat údaje z GPX - Má hodnoty</title>", html);
        Assert.Contains("<h1 class=\"mb-3\">Přepsat údaje z GPX?</h1>", html);
        Assert.Contains("(0.33 km, ↑300 m)", html);
        Assert.Contains("name=\"fields\"", html);
        Assert.Contains("value=\"meters_ascend\" id=\"field_meters_ascend\" checked", html);
        Assert.Contains("<td class=\"text-muted\">100 m</td>", html);
        Assert.Contains("<td><strong>300 m</strong></td>", html);
        Assert.Contains("<td class=\"text-muted\">2.0 h</td>", html);
        Assert.Contains("<td><strong>0.5 h</strong></td>", html);
        Assert.DoesNotContain("value=\"meters_descend\"", html);
        Assert.Contains("Přepsat vybrané", html);
        Assert.Contains($"href=\"/trips/trip/{id}/edit/\" class=\"btn btn-outline-secondary\"", html);
    }

    [Fact]
    public async Task Upload_Gpx_DifferentValues_FlashesFilledCountOnApplyPage()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        var id = await factory.Trips().CreateAsync(new TripFormData { Title = "Má hodnoty", MetersAscend = 100 });

        var res = await admin.PostFormAsync(EditUrl(id), EditUrl(id), [("title", "Má hodnoty"), ("meters_ascend", "100")], Gpx(StatsGpx));
        var html = Decoded(await admin.GetStringAsync(res.Headers.Location!.OriginalString, Ct));

        Assert.Contains("Prázdná pole (5) jsou předvyplněná z GPX.", html);
        Assert.Contains("Trasa nahrána: 0.33 km, ↑300 m.", html);
    }

    [Fact]
    public async Task Apply_WritesOnlySelectedFields()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        var stats = GpxParser.Parse(StatsGpx).Stats;
        var id = await factory.Trips().CreateAsync(new TripFormData
        {
            Title = "Potvrzení",
            MetersAscend = 100,
            MetersDescend = 50,
            LengthHours = 2.0,
            TrackJson = stats.ToJson(),
        });
        var url = $"/trips/trip/{id}/track/apply/";

        var res = await admin.PostFormAsync(url, url, [("fields", "meters_ascend"), ("fields", "bogus_field")]);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal(EditUrl(id), res.Headers.Location!.OriginalString);
        var raw = await factory.RawTripAsync(id);
        Assert.Equal(300, Assert.IsType<int>(raw["MetersAscend"]));
        Assert.Equal(50, Assert.IsType<int>(raw["MetersDescend"]));
        Assert.Equal(2.0, raw["LengthHours"]);
        Assert.Contains("Přepsáno z GPX: Převýšení nahoru.", Decoded(await admin.GetStringAsync(EditUrl(id), Ct)));
    }

    [Fact]
    public async Task Apply_NothingSelected_LeavesValuesAlone()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        var id = await factory.Trips().CreateAsync(new TripFormData
        {
            Title = "Beze změny",
            MetersAscend = 100,
            TrackJson = GpxParser.Parse(StatsGpx).Stats.ToJson(),
        });
        var url = $"/trips/trip/{id}/track/apply/";

        var res = await admin.PostFormAsync(url, url, []);

        Assert.Equal(EditUrl(id), res.Headers.Location!.OriginalString);
        Assert.Equal(100, (await factory.RawTripAsync(id))["MetersAscend"]);
        Assert.Contains("Hodnoty zůstaly beze změny.", Decoded(await admin.GetStringAsync(EditUrl(id), Ct)));
    }

    [Fact]
    public async Task Apply_NoConflicts_RedirectsToEdit()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        var id = await factory.Trips().CreateAsync(new TripFormData { Title = "Bez trasy" });

        var res = await admin.GetAsync($"/trips/trip/{id}/track/apply/", Ct);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal(EditUrl(id), res.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Apply_UnknownTrip_Returns404()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        var res = await admin.GetAsync("/trips/trip/nope/track/apply/", Ct);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Upload_BrokenGpx_ShowsErrorAndSavesNothing()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        var res = await admin.PostFormAsync(NewUrl, NewUrl, [("title", "Rozbité")],
            Gpx("<gpx><trk><trkseg>"u8.ToArray()),
            Photo(TestImages.Jpeg(4, 4), "a.jpg"));
        var html = Decoded(await res.Content.ReadAsStringAsync(Ct));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("<div class=\"alert alert-danger\" role=\"alert\">", html);
        Assert.Contains("Soubor se nepodařilo přečíst jako GPX", html);
        Assert.Contains("Rozbité", html);
        Assert.Empty(await factory.Trips().ListAsync());
        Assert.Equal(0, await factory.BlobCountAsync(factory.TracksContainer));
        Assert.Equal(0, await factory.BlobCountAsync(factory.PhotosContainer));
    }

    [Fact]
    public async Task Upload_BrokenGpx_OnExistingTrip_ChangesNothing()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        var id = await factory.Trips().CreateAsync(new TripFormData { Title = "Původní", MetersAscend = 10 });
        var before = await factory.RawTripAsync(id);

        var res = await admin.PostFormAsync(EditUrl(id), EditUrl(id), [("title", "Změněné"), ("meters_ascend", "99")],
            Gpx("not xml at all"u8.ToArray()));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Soubor se nepodařilo přečíst jako GPX", Decoded(await res.Content.ReadAsStringAsync(Ct)));
        var after = await factory.RawTripAsync(id);
        Assert.Equal(before.ETag, after.ETag);
        Assert.Equal("Původní", after["Title"]);
        Assert.Equal(0, await factory.BlobCountAsync(factory.TracksContainer));
    }

    [Fact]
    public async Task Upload_EmptyTrackGpx_ShowsParserMessage()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        var empty = "<?xml version=\"1.0\"?><gpx version=\"1.1\" xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg/></trk></gpx>"u8.ToArray();

        var res = await admin.PostFormAsync(NewUrl, NewUrl, [("title", "Prázdná")], Gpx(empty));

        Assert.Contains("GPX soubor neobsahuje žádné body trasy.", Decoded(await res.Content.ReadAsStringAsync(Ct)));
        Assert.Empty(await factory.Trips().ListAsync());
    }

    [Fact]
    public async Task Upload_BrokenGpx_InProduction_DoesNotLeakParserDetails()
    {
        await using var factory = new AppFactory(azurite).WithEnvironment("Production");
        var admin = await TripEditTests.SecureClientAsync(factory);

        var res = await admin.PostFormAsync(NewUrl, NewUrl, [("title", "Rozbité")], Gpx("<gpx><trk><trkseg>"u8.ToArray()));
        var html = Decoded(await res.Content.ReadAsStringAsync(Ct));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Soubor se nepodařilo přečíst jako GPX", html);
        Assert.DoesNotContain("Line 1, position", html);
        Assert.DoesNotContain("XmlException", html);
    }

    [Fact]
    public async Task Upload_WrongExtension_Rejected()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        var res = await admin.PostFormAsync(NewUrl, NewUrl, [("title", "Špatná přípona")], Gpx(StatsGpx, "track.txt"));
        var html = Decoded(await res.Content.ReadAsStringAsync(Ct));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Nahraj soubor ve formátu .gpx (v Garmin Connect: aktivita → ⚙ → Export to GPX).", html);
        Assert.Contains("<ul class=\"errorlist\">", html);
        Assert.Empty(await factory.Trips().ListAsync());
        Assert.Equal(0, await factory.BlobCountAsync(factory.TracksContainer));
    }

    [Fact]
    public async Task Upload_UppercaseExtension_Accepted()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        var res = await admin.PostFormAsync(NewUrl, NewUrl, [("title", "Velká písmena")], Gpx(StatsGpx, "TRACK.GPX"));

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.NotNull(await factory.Tracks().GetGpxAsync(IdFrom(res)));
    }

    [Fact]
    public async Task Upload_GpxOver25MB_Rejected()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        var big = new byte[25 * 1024 * 1024 + 1];
        Array.Fill(big, (byte)'a');

        var res = await admin.PostFormAsync(NewUrl, NewUrl, [("title", "Obrovské")], Gpx(big));
        var html = Decoded(await res.Content.ReadAsStringAsync(Ct));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("GPX soubor je příliš velký (max 25 MB).", html);
        Assert.Empty(await factory.Trips().ListAsync());
        Assert.Equal(0, await factory.BlobCountAsync(factory.TracksContainer));
    }

    [Fact]
    public async Task Save_WithoutGpx_TouchesNoBlob()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        var gpx = StatsGpx;
        var parsed = GpxParser.Parse(gpx);
        var id = await factory.Trips().CreateAsync(new TripFormData { Title = "Se stopou", TrackJson = parsed.Stats.ToJson() });
        await factory.Tracks().SaveAsync(id, gpx, Encoding.UTF8.GetBytes(parsed.GeoJson));

        var res = await admin.PostFormAsync(EditUrl(id), EditUrl(id), [("title", "Se stopou 2")]);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal(gpx, await factory.Tracks().GetGpxAsync(id));
        Assert.Equal(parsed.Stats.ToJson(), (await factory.Trips().GetAsync(id))!.TrackJson);
        Assert.Equal(2, await factory.BlobCountAsync(factory.TracksContainer));
    }

    [Fact]
    public async Task Upload_Heic_StoredAsJpeg()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        var res = await admin.PostFormAsync(NewUrl, NewUrl, [("title", "Z iPhonu")], Photo(TestImages.Heic(), "IMG_0001.HEIC"));

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        var id = IdFrom(res);
        var photo = Assert.Single(await factory.Services.GetRequiredService<IPhotoStore>().ListAsync(id));
        Assert.EndsWith(".jpg", photo);
        Assert.DoesNotContain("heic", photo, StringComparison.OrdinalIgnoreCase);
        var blob = factory.Services.GetRequiredService<Azure.Storage.Blobs.BlobServiceClient>()
            .GetBlobContainerClient(factory.PhotosContainer)
            .GetBlobClient($"{id}/{photo[(photo.LastIndexOf('/') + 1)..]}");
        Assert.Equal("image/jpeg", (await blob.GetPropertiesAsync(cancellationToken: Ct)).Value.ContentType);
    }

    [Fact]
    public async Task Upload_MultiplePhotos_AllStored()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        var res = await admin.PostFormAsync(NewUrl, NewUrl, [("title", "Víc fotek")],
            Photo(TestImages.Jpeg(4, 4), "a.jpg"), Photo(TestImages.Jpeg(5, 5), "b.jpg"), Photo(TestImages.Jpeg(6, 6), "c.jpg"));

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal(3, (await factory.Services.GetRequiredService<IPhotoStore>().ListAsync(IdFrom(res))).Count);
    }

    [Fact]
    public async Task Upload_CorruptPhoto_TripStillSaved()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        var res = await admin.PostFormAsync(NewUrl, NewUrl, [("title", "Poškozená fotka")],
            Photo("junk"u8.ToArray(), "broken.jpg"), Photo(TestImages.Jpeg(4, 4), "good.jpg"));

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        var id = IdFrom(res);
        Assert.Equal("Poškozená fotka", (await factory.Trips().GetAsync(id))!.Title);
        Assert.Single(await factory.Services.GetRequiredService<IPhotoStore>().ListAsync(id));
        var html = Decoded(await admin.GetStringAsync(res.Headers.Location!.OriginalString, Ct));
        Assert.Contains("broken.jpg", html);
        Assert.Contains("alert alert-warning", html);
    }

    [Fact]
    public async Task Upload_Photo_OnEdit_ShowsInExistingPhotos()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        var id = await factory.Trips().CreateAsync(new TripFormData { Title = "Fotky" });

        await admin.PostFormAsync(EditUrl(id), EditUrl(id), [("title", "Fotky")], Photo(TestImages.Jpeg(4, 4), "a.jpg"));
        var html = await admin.GetStringAsync(EditUrl(id), Ct);

        Assert.Contains("Existing Photos", html);
        Assert.Contains("class=\"card-img-top\" alt=\"Trip photo\"", html);
        Assert.Contains($"/{factory.PhotosContainer}/{id}/", html);
    }

    [Fact]
    public async Task TrackDelete_ClearsStatsAndBlobs()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        var gpx = StatsGpx;
        var parsed = GpxParser.Parse(gpx);
        var id = await factory.Trips().CreateAsync(new TripFormData
        {
            Title = "Mazání trasy",
            MetersAscend = 300,
            TrackJson = parsed.Stats.ToJson(),
        });
        await factory.Tracks().SaveAsync(id, gpx, Encoding.UTF8.GetBytes(parsed.GeoJson));

        var res = await admin.PostFormAsync($"/trips/trip/{id}/track/delete/", EditUrl(id), []);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal(EditUrl(id), res.Headers.Location!.OriginalString);
        var raw = await factory.RawTripAsync(id);
        Assert.Equal("", Assert.IsType<string>(raw["TrackJson"]));
        Assert.Equal(300, raw["MetersAscend"]);
        Assert.Null(await factory.Tracks().GetGpxAsync(id));
        Assert.Null(await factory.Tracks().GetGeoJsonAsync(id));
        var html = Decoded(await admin.GetStringAsync(EditUrl(id), Ct));
        Assert.Contains("Trasa byla smazána.", html);
        Assert.DoesNotContain("trackDeleteForm", html);
        Assert.Contains("Nahrát GPX", html);
    }

    [Fact]
    public async Task TrackDelete_Get_IsNotAllowed()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        var id = await factory.Trips().CreateAsync(new TripFormData { Title = "x" });

        var res = await admin.GetAsync($"/trips/trip/{id}/track/delete/", Ct);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
    }

    [Fact]
    public async Task TrackDelete_UnknownTrip_Returns404()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        var res = await admin.PostFormAsync("/trips/trip/nope/track/delete/", "/trips/trip/new/", []);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
