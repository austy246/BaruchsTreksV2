using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using BaruchsTreks.Photos;
using BaruchsTreks.Tracks;
using BaruchsTreks.Trips;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BaruchsTreks.Tests.Integration;

public class ReadPagesTests(AzuriteFixture azurite)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebApplicationFactoryClientOptions NoRedirect => new() { AllowAutoRedirect = false };

    private static Task<string> AddTripAsync(AppFactory factory, TripFormData data) =>
        factory.Services.GetRequiredService<ITripRepository>().CreateAsync(data);

    private static string Decoded(string html) => WebUtility.HtmlDecode(html);

    private static async Task AddRawPhotoAsync(AppFactory factory, string tripId, string blobName)
    {
        var blobs = factory.Services.GetRequiredService<BlobServiceClient>();
        var container = blobs.GetBlobContainerClient(factory.PhotosContainer);
        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: Ct);
        await container.GetBlobClient($"{tripId}/{blobName}").UploadAsync(
            new MemoryStream(TestImages.Png(4, 4)), overwrite: true, cancellationToken: Ct);
    }

    [Fact]
    public async Task Root_RedirectsToTrips()
    {
        await using var factory = new AppFactory(azurite);

        var res = await factory.CreateClient(NoRedirect).GetAsync("/", Ct);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal("/trips/", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Index_ShowsSixNewestWithFirstPhoto()
    {
        await using var factory = new AppFactory(azurite);
        var ids = new List<string>();
        for (var i = 1; i <= 8; i++)
        {
            ids.Add(await AddTripAsync(factory, new TripFormData
            {
                Title = $"Trip {i}",
                Description = i == 8 ? string.Join(' ', Enumerable.Range(1, 40).Select(n => $"w{n}")) : "short",
                LengthHours = 4.5,
                TripCompletedOn = i == 8 ? new DateOnly(2024, 3, 9) : null,
            }));
        }

        var photoUrl = await factory.Services.GetRequiredService<IPhotoStore>()
            .UploadAsync(ids[7], new MemoryStream(TestImages.Png(4, 4)), "a.png");
        await AddRawPhotoAsync(factory, ids[7], "99999999999999.png");

        var res = await factory.CreateClient().GetAsync("/trips/", Ct);
        var html = await res.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/html; charset=utf-8", res.Content.Headers.ContentType?.ToString());
        for (var i = 3; i <= 8; i++)
            Assert.Contains($">Trip {i}</h5>", html);
        Assert.DoesNotContain(">Trip 1</h5>", html);
        Assert.DoesNotContain(">Trip 2</h5>", html);
        Assert.True(html.IndexOf(">Trip 8</h5>", StringComparison.Ordinal) < html.IndexOf(">Trip 3</h5>", StringComparison.Ordinal));
        Assert.Contains($"href=\"/trips/trip/{ids[7]}/\"", html);
        Assert.Contains($"<img src=\"{photoUrl}\" class=\"card-img-top\" alt=\"Trip 8\"", html);
        Assert.Single(Regex.Matches(html, "<img src="));
        Assert.Equal(5, Regex.Matches(html, "bi bi-image text-muted").Count);
        Assert.Contains("<small>Completed: 2024-03-09</small>", html);
        Assert.Contains("w30 …", Decoded(html));
        Assert.DoesNotContain("w31", html);
        Assert.Contains("4.5 hours", html);
        Assert.Contains("Welcome to Baruch's Treks", Decoded(html));
        Assert.Contains("href=\"/trips/all/\"", html);
        Assert.DoesNotContain("Create New Trip", html);
    }

    [Fact]
    public async Task Index_NoTrips_ShowsEmptyMessage()
    {
        await using var factory = new AppFactory(azurite);

        var html = await factory.CreateClient().GetStringAsync("/trips/", Ct);

        Assert.Contains("No trips available at the moment. Check back later!", html);
    }

    [Fact]
    public async Task Index_EscapesTitles()
    {
        await using var factory = new AppFactory(azurite);
        await AddTripAsync(factory, new TripFormData { Title = "<script>alert(1)</script>" });

        var html = await factory.CreateClient().GetStringAsync("/trips/", Ct);

        Assert.DoesNotContain("<script>alert(1)", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
    }

    [Fact]
    public async Task AllTrips_FilterAndSort()
    {
        await using var factory = new AppFactory(azurite);
        await AddTripAsync(factory, new TripFormData { Title = "banana", TripCompletedOn = new DateOnly(2024, 1, 10), MetersAscend = 850 });
        await AddTripAsync(factory, new TripFormData { Title = "Apple", TripCompletedOn = new DateOnly(2023, 5, 1) });
        await AddTripAsync(factory, new TripFormData { Title = "cherry" });
        var client = factory.CreateClient();

        var all = await client.GetStringAsync("/trips/all/", Ct);
        Assert.Contains("All Trips <span class=\"badge bg-secondary\">3</span>", all);
        Assert.Contains("Completed <span class=\"badge bg-secondary\">2</span>", all);
        Assert.Contains("Future Trips <span class=\"badge bg-secondary\">1</span>", all);
        Assert.Contains("↑ 850m", all);
        Assert.Contains("Future Trip</small>", all);
        Assert.Contains("card shadow-sm border-info", all);

        // Výchozí řazení: poslední změna sestupně.
        Assert.Equal(["cherry", "Apple", "banana"], Titles(all));

        var byName = await client.GetStringAsync("/trips/all/?filter=all&sort=name&dir=asc", Ct);
        Assert.Equal(["Apple", "banana", "cherry"], Titles(byName));
        Assert.Contains("href=\"/trips/all/?filter=all&sort=name&dir=desc\" title=\"Descending\"", byName);

        var completedAsc = await client.GetStringAsync("/trips/all/?filter=completed&sort=completed&dir=asc", Ct);
        Assert.Equal(["Apple", "banana"], Titles(completedAsc));
        Assert.Contains("btn btn-outline-success active", completedAsc);

        var completedDesc = await client.GetStringAsync("/trips/all/?filter=completed&sort=completed&dir=desc", Ct);
        Assert.Equal(["banana", "Apple"], Titles(completedDesc));

        var future = await client.GetStringAsync("/trips/all/?filter=future", Ct);
        Assert.Equal(["cherry"], Titles(future));

        var created = await client.GetStringAsync("/trips/all/?sort=created&dir=asc", Ct);
        Assert.Equal(["banana", "Apple", "cherry"], Titles(created));

        // Neznámý filtr a řazení: všechno, výchozí řazení.
        var odd = await client.GetStringAsync("/trips/all/?filter=zzz&sort=zzz&dir=zzz", Ct);
        Assert.Equal(["cherry", "Apple", "banana"], Titles(odd));
        Assert.DoesNotContain("btn-outline-primary active", odd);
    }

    [Theory]
    [InlineData("completed", "No completed trips available. Check back later!")]
    [InlineData("future", "No future trips planned yet. Check back later!")]
    [InlineData("all", "No trips available at the moment. Check back later!")]
    public async Task AllTrips_Empty_ShowsFilterSpecificMessage(string filter, string message)
    {
        await using var factory = new AppFactory(azurite);

        var html = await factory.CreateClient().GetStringAsync($"/trips/all/?filter={filter}", Ct);

        Assert.Contains(message, html);
    }

    private static List<string> Titles(string html) =>
        Regex.Matches(html, "<h5 class=\"card-title\">(.*?)</h5>").Select(m => m.Groups[1].Value).ToList();

    [Fact]
    public async Task Detail_ShowsPhotosAndTrackStats()
    {
        await using var factory = new AppFactory(azurite).WithSetting("Mapy:ApiKey", "my-key");
        var stats = new TrackStats
        {
            DistanceKm = 12.0,
            MetersAscend = 640,
            MetersDescend = 600,
            PointCount = 2,
            OriginalPointCount = 2,
            MinElevation = 900,
            MaxElevation = 1555,
            Bounds = [[50.0, 14.0], [50.1, 14.1]],
        };
        var id = await AddTripAsync(factory, new TripFormData
        {
            Title = "Sněžka",
            Description = "Long climb",
            TripCompletedOn = new DateOnly(2024, 2, 3),
            LengthHours = 5,
            MetersAscend = 640,
            MetersDescend = 600,
            UiaaGrade = "III",
            AlpineGrade = "",
            Participants = "Jan, Eva",
            Location = "Krkonoše",
            Difficulty = "none",
            ParkingJson = "{\"Latitude\": 50.7, \"Longtitude\": 15.7}",
            HighPointJson = "{\"Latitude\": 50.736, \"Longtitude\": 15.74}",
            TrackJson = stats.ToJson(),
        });
        await AddRawPhotoAsync(factory, id, "20240101000001.png");
        await AddRawPhotoAsync(factory, id, "20240101000002.png");

        var res = await factory.CreateClient().GetAsync($"/trips/trip/{id}/", Ct);
        var html = await res.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("<title>Sněžka - Baruch&#x27;s Treks</title>", html);
        Assert.Contains("<h1 class=\"mb-3\">Sněžka</h1>", html);
        Assert.Contains("Completed on: 2024-02-03", html);
        Assert.Contains("Long climb", html);
        Assert.Equal(2, Regex.Matches(html, "alt=\"Trip photo\"").Count);
        Assert.Contains($"http://blob.test/{factory.PhotosContainer}/{id}/20240101000001.png", html);
        Assert.Contains("data-lightbox=\"trip-photos\"", html);
        Assert.Contains("Jan, Eva", html);
        Assert.Contains("5.0 hours", html);
        Assert.Contains("12.0 km", html);
        Assert.Contains("1555 m", html);
        Assert.Contains("640 meters", html);
        Assert.Contains("600 meters", html);
        Assert.Contains("UIAA Grade:</strong>", html);
        Assert.Contains("III", html);
        Assert.DoesNotContain("Alpine Grade", html);
        Assert.DoesNotContain("Difficulty:", html);
        Assert.Contains("Krkonoše", html);
        Assert.Contains($"href=\"/trips/trip/{id}/track.gpx\"", html);
        Assert.Contains("id=\"elevationProfile\"", html);
        Assert.Contains($"fetch('/trips/trip/{id}/track.geojson')", html);
        Assert.Contains("const API_KEY = 'my-key';", html);
        Assert.Contains("JSON.parse('{\\u0022Latitude\\u0022: 50.7, \\u0022Longtitude\\u0022: 15.7}')", html);
        Assert.Contains("https://unpkg.com/leaflet@1.9.4/dist/leaflet.js", html);
        Assert.Contains("/img/marker-summit.svg", html);
        // Návštěvník bez přihlášení nevidí akce správce.
        Assert.DoesNotContain("Edit Trip", html);
        Assert.DoesNotContain("Delete Trip", html);
    }

    [Fact]
    public async Task Detail_WithoutMapData_HidesMapCard()
    {
        await using var factory = new AppFactory(azurite);
        var id = await AddTripAsync(factory, new TripFormData { Title = "Plain" });

        var html = await factory.CreateClient().GetStringAsync($"/trips/trip/{id}/", Ct);

        Assert.DoesNotContain("<div id=\"map\"", html);
        Assert.DoesNotContain("L.map(", html);
        Assert.Contains("Back to All Trips", html);
    }

    [Fact]
    public async Task Detail_InvalidTrackJson_IsTreatedAsNoTrack()
    {
        await using var factory = new AppFactory(azurite);
        var id = await AddTripAsync(factory, new TripFormData { Title = "Broken", TrackJson = "{not json" });

        var res = await factory.CreateClient().GetAsync($"/trips/trip/{id}/", Ct);
        var html = await res.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.DoesNotContain("track.geojson", html);
        Assert.DoesNotContain("Distance:", html);
    }

    [Fact]
    public async Task Detail_AdminSeesEditActions()
    {
        await using var factory = new AppFactory(azurite);
        var id = await AddTripAsync(factory, new TripFormData { Title = "Mine" });
        var admin = await factory.CreateUserClientAsync(isAdmin: true);

        var html = await admin.GetStringAsync($"/trips/trip/{id}/", Ct);

        Assert.Contains($"href=\"/trips/trip/{id}/edit/\"", html);
        Assert.Contains($"href=\"/trips/trip/{id}/copy/\"", html);
        Assert.Contains($"<form action=\"/trips/trip/{id}/delete/\" method=\"get\"", html);
    }

    [Fact]
    public async Task Detail_UnknownId_Returns404()
    {
        await using var factory = new AppFactory(azurite);

        var res = await factory.CreateClient().GetAsync("/trips/trip/does-not-exist/", Ct);
        var html = await res.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Contains("Trip Not Found", html);
        Assert.Contains("Sorry, the trip you're looking for doesn't exist or has been removed.", Decoded(html));
        Assert.Contains("href=\"/trips/all/\"", html);
    }

    [Theory]
    [InlineData("a'b")]
    [InlineData("a%27b")]
    [InlineData("a%23b")]
    [InlineData("a%3Fb")]
    [InlineData("a%5Cb")]
    [InlineData("a%2Fb")]
    [InlineData("%20")]
    [InlineData("trip_00000000000000")]
    public async Task Detail_WeirdId_Returns404(string id)
    {
        await using var factory = new AppFactory(azurite);
        var client = factory.CreateClient();

        var res = await client.GetAsync($"/trips/trip/{id}/", Ct);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/trips/trip/{id}/track.geojson", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/trips/trip/{id}/track.gpx", Ct)).StatusCode);
    }

    [Fact]
    public async Task TrackGeoJson_ServedToAnonymous()
    {
        await using var factory = new AppFactory(azurite);
        var id = await AddTripAsync(factory, new TripFormData { Title = "T" });
        var payload = Encoding.UTF8.GetBytes("{\"type\":\"Feature\"}");
        await factory.Services.GetRequiredService<ITrackStore>().SaveAsync(id, Encoding.UTF8.GetBytes("<gpx/>"), payload);

        var res = await factory.CreateClient().GetAsync($"/trips/trip/{id}/track.geojson", Ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/geo+json", res.Content.Headers.ContentType?.ToString());
        Assert.Equal("public, max-age=3600", res.Headers.CacheControl?.ToString());
        Assert.Equal(payload, await res.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task TrackGpx_IsAttachment()
    {
        await using var factory = new AppFactory(azurite);
        var id = await AddTripAsync(factory, new TripFormData { Title = "T" });
        var gpx = Encoding.UTF8.GetBytes("<gpx>x</gpx>");
        await factory.Services.GetRequiredService<ITrackStore>().SaveAsync(id, gpx, Encoding.UTF8.GetBytes("{}"));

        var res = await factory.CreateClient().GetAsync($"/trips/trip/{id}/track.gpx", Ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/gpx+xml", res.Content.Headers.ContentType?.ToString());
        Assert.Equal($"attachment; filename=\"{id}.gpx\"", res.Content.Headers.GetValues("Content-Disposition").Single());
        Assert.Equal(gpx, await res.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task TrackMissing_Returns404()
    {
        await using var factory = new AppFactory(azurite);
        var id = await AddTripAsync(factory, new TripFormData { Title = "No track" });
        var client = factory.CreateClient();

        var geojson = await client.GetAsync($"/trips/trip/{id}/track.geojson", Ct);
        var gpx = await client.GetAsync($"/trips/trip/{id}/track.gpx", Ct);

        Assert.Equal(HttpStatusCode.NotFound, geojson.StatusCode);
        Assert.Contains("No track for this trip", await geojson.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.NotFound, gpx.StatusCode);
        Assert.Equal("No track for this trip", await gpx.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Layout_ShowsLoginOrLogoutLink()
    {
        await using var factory = new AppFactory(azurite);

        var anonymous = await factory.CreateClient().GetStringAsync("/trips/", Ct);
        Assert.Contains("<a class=\"nav-link\" href=\"/accounts/login/\">Login</a>", anonymous);
        Assert.DoesNotContain("Logout", anonymous);
        Assert.DoesNotContain("New Trip", anonymous);
        Assert.Contains("https://cdn.jsdelivr.net/npm/bootstrap@5.3.0/dist/css/bootstrap.min.css", anonymous);
        Assert.Contains("https://cdn.jsdelivr.net/npm/bootstrap-icons@1.11.0/font/bootstrap-icons.css", anonymous);
        Assert.Contains("https://cdn.jsdelivr.net/npm/bootstrap@5.3.0/dist/js/bootstrap.bundle.min.js", anonymous);
        Assert.Contains("href=\"/css/style.css\"", anonymous);
        Assert.Contains($"&copy; {DateTime.UtcNow.Year} Baruch's Treks. All rights reserved.", anonymous);

        var user = await factory.CreateUserClientAsync(isAdmin: false);
        var userHtml = await user.GetStringAsync("/trips/all/", Ct);
        Assert.Contains("<form method=\"post\" action=\"/accounts/logout/\" class=\"logout-form\">", userHtml);
        Assert.Contains("name=\"__RequestVerificationToken\"", userHtml);
        Assert.Contains("<button type=\"submit\" class=\"dropdown-item\">Logout</button>", userHtml);
        Assert.Contains("user-", userHtml);
        Assert.DoesNotContain(">Login</a>", userHtml);
        Assert.DoesNotContain("href=\"/trips/trip/new/\"", userHtml);
        Assert.DoesNotContain("Import trasy", userHtml);

        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        var adminHtml = await admin.GetStringAsync("/trips/all/", Ct);
        Assert.Contains("href=\"/trips/trip/new/\"", adminHtml);
        Assert.Contains("Import trasy", adminHtml);
        Assert.Contains("nav-link active\" href=\"/trips/all/\"", adminHtml);
    }

    [Fact]
    public async Task Layout_LogoutFormWorks()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        var token = await admin.GetAntiforgeryTokenAsync("/trips/", Ct);

        var res = await admin.PostAsync("/accounts/logout/", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = token }), Ct);

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal("/trips/", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Layout_RendersFlashMessagesOnce()
    {
        await using var factory = new AppFactory(azurite);
        var client = factory.CreateClient();

        var first = await client.GetStringAsync("/__flash?m=success:Saved%20it&m=error:Oops%20%3Cb%3E&m=info:Plain", Ct);
        var second = await client.GetStringAsync("/trips/", Ct);

        Assert.Contains("alert alert-success alert-dismissible fade show", first);
        Assert.Contains("Saved it", first);
        Assert.Contains("alert alert-error alert-dismissible fade show", first);
        Assert.Contains("Oops &lt;b&gt;", first);
        Assert.Contains("alert alert-info alert-dismissible fade show", first);
        Assert.Contains("data-bs-dismiss=\"alert\" aria-label=\"Close\"", first);
        Assert.DoesNotContain("Saved it", second);
    }

    [Fact]
    public async Task StaticFiles_AreServed()
    {
        await using var factory = new AppFactory(azurite);
        var client = factory.CreateClient();

        var css = await client.GetAsync("/css/style.css", Ct);
        var svg = await client.GetAsync("/img/marker-parking.svg", Ct);

        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, svg.StatusCode);
    }

    [Fact]
    public async Task UnknownUrl_ShowsNotFoundPage()
    {
        await using var factory = new AppFactory(azurite);

        var res = await factory.CreateClient().GetAsync("/no/such/page", Ct);
        var html = await res.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Contains("Trip Not Found", html);
        Assert.Contains("Back to All Trips", html);
    }

    private sealed class ThrowingTripRepository : ITripRepository
    {
        private const string Secret = "secret-storage-detail";

        public Task<IReadOnlyList<Trip>> ListAsync() => throw new InvalidOperationException(Secret);
        public Task<Trip?> GetAsync(string id) => throw new InvalidOperationException(Secret);
        public Task<string> CreateAsync(TripFormData data) => throw new InvalidOperationException(Secret);
        public Task UpdateAsync(string id, TripFormData data) => throw new InvalidOperationException(Secret);
        public Task MergeFieldsAsync(string id, IReadOnlyDictionary<string, object?> fields) => throw new InvalidOperationException(Secret);
        public Task DeleteAsync(string id) => throw new InvalidOperationException(Secret);
    }

    [Theory]
    [InlineData("/trips/")]
    [InlineData("/trips/all/")]
    [InlineData("/trips/trip/trip_20240101000000/")]
    public async Task Error_InProduction_ShowsGenericPageWithoutDetails(string path)
    {
        await using var factory = new AppFactory(azurite)
            .WithEnvironment("Production")
            .ConfigureTestServices(services => services.AddSingleton<ITripRepository, ThrowingTripRepository>());

        var res = await factory.CreateClient().GetAsync(path, Ct);
        var html = await res.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.DoesNotContain("secret-storage-detail", html);
        Assert.DoesNotContain("InvalidOperationException", html);
        Assert.DoesNotContain("   at ", html);
        Assert.DoesNotContain("Technical Details", html);
        Assert.Contains("href=\"/trips/\"", html);
        Assert.Contains("something went wrong", Decoded(html));
    }

    [Fact]
    public async Task Error_Production_ServerErrorPageShowsTimeAndPage()
    {
        await using var factory = new AppFactory(azurite)
            .WithEnvironment("Production")
            .ConfigureTestServices(services => services.AddSingleton<ITripRepository, ThrowingTripRepository>());

        var res = await factory.CreateClient().GetAsync("/trips/all/", Ct);
        var html = await res.Content.ReadAsStringAsync(Ct);

        Assert.Contains("Server Error", html);
        Assert.Contains("<li>Page: /trips/all/</li>", html);
        Assert.Matches(@"<li>Time: \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}</li>", html);
    }

    [Fact]
    public async Task Error_IndexInDevelopment_ShowsDetails()
    {
        await using var factory = new AppFactory(azurite)
            .ConfigureTestServices(services => services.AddSingleton<ITripRepository, ThrowingTripRepository>());

        var res = await factory.CreateClient().GetAsync("/trips/", Ct);
        var html = await res.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Contains("Error Occurred", html);
        Assert.Contains("Technical Details", html);
        Assert.Contains("secret-storage-detail", html);
    }
}
