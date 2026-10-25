using System.Text;
using BaruchsTreks.Tracks;
using BaruchsTreks.Trips;
using Microsoft.Extensions.DependencyInjection;

namespace BaruchsTreks.Tests.Integration;

public class CultureTests(AzuriteFixture azurite)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Detail_UnderCzechCulture_UsesDotDecimals()
    {
        await using var factory = new AppFactory(azurite).WithCulture("cs-CZ");
        var stats = new TrackStats
        {
            DistanceKm = 12.34,
            MetersAscend = 1234,
            MetersDescend = 1200,
            PointCount = 3,
            OriginalPointCount = 3,
            MinElevation = 900,
            MaxElevation = 1555,
            Bounds = [[50.5, 14.5], [50.6, 14.6]],
        };
        var id = await factory.Services.GetRequiredService<ITripRepository>().CreateAsync(new TripFormData
        {
            Title = "Czech",
            LengthHours = 4.5,
            MetersAscend = 1234,
            ParkingJson = "{\"Latitude\": 50.0755, \"Longtitude\": 14.4378}",
            HighPointJson = "{\"Latitude\": 50.1, \"Longtitude\": 14.5}",
            TrackJson = stats.ToJson(),
        });
        await factory.Services.GetRequiredService<ITrackStore>().SaveAsync(
            id,
            Encoding.UTF8.GetBytes("<gpx/>"),
            Encoding.UTF8.GetBytes("{\"type\":\"Feature\",\"geometry\":{\"type\":\"LineString\",\"coordinates\":[[14.5,50.5,900.5],[14.6,50.6,1555.25]]}}"));
        var client = factory.CreateClient();

        var res = await client.GetAsync($"/trips/trip/{id}/", Ct);
        var html = await res.Content.ReadAsStringAsync(Ct);
        var geojson = await client.GetAsync($"/trips/trip/{id}/track.geojson", Ct);
        var geojsonText = await geojson.Content.ReadAsStringAsync(Ct);

        // Kultura opravdu platí (jinak by test nic neprokazoval).
        Assert.Equal("1,5", res.Headers.GetValues("X-Test-Decimal").Single());
        Assert.Contains("4.5 hours", html);
        Assert.Contains("12.34 km", html);
        Assert.Contains("1234 meters", html);
        Assert.Contains("50.0755", html);
        Assert.Contains("14.4378", html);
        Assert.DoesNotContain("4,5", html);
        Assert.DoesNotContain("12,34", html);
        Assert.DoesNotContain("50,0755", html);
        Assert.Contains("900.5", geojsonText);
        Assert.Contains("1555.25", geojsonText);
    }
}
