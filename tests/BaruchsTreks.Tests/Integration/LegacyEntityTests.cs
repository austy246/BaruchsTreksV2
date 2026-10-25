using BaruchsTreks.Trips;

namespace BaruchsTreks.Tests.Integration;

public class LegacyEntityTests(AzuriteFixture azurite)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task List_ReadsAllLegacyShapes()
    {
        var storage = new TestStorage(azurite);
        var repo = await storage.CreateRepositoryAsync();
        await storage.TripsTableClient.AddEntityAsync(LegacyEntities.A("trip_20250101000000"), Ct);
        await storage.TripsTableClient.AddEntityAsync(LegacyEntities.B("trip_20240101000000"), Ct);

        var list = await repo.ListAsync();

        Assert.Equal(2, list.Count);
        var a = list.Single(t => t.Id == "trip_20250101000000");
        var b = list.Single(t => t.Id == "trip_20240101000000");

        Assert.Equal("Legacy A", a.Title);
        Assert.Equal("2025-08-15", a.TripCompletedOn);
        Assert.Equal(6, a.LengthHours);
        Assert.Equal(1200, a.ElevationGain);
        Assert.Equal(0, a.MetersDescend);
        Assert.Equal("", a.Participants);
        Assert.Equal("none", a.UiaaGrade);
        Assert.Equal("PD", a.AlpineGrade);
        Assert.Equal("none", a.TripClass);
        Assert.Contains("Longtitude", a.ParkingJson);

        Assert.Equal("Untitled Trip", b.Title);
        Assert.Equal("2024-07-01", b.TripCompletedOn);
        Assert.Equal(new DateTimeOffset(2024, 7, 1, 0, 0, 0, TimeSpan.Zero), b.CompletedAt);
        Assert.Equal(4.5, b.LengthHours);
        Assert.Equal(800, b.ElevationGain);
        Assert.Equal(810.5, b.MetersAscend);
        Assert.Equal(805, b.MetersDescend);
        Assert.Equal("", b.Location);
        Assert.Equal("IV", b.UiaaGrade);
        Assert.Equal("Trail", b.TripClass);
        Assert.Equal("""{"distance_km": 12.3}""", b.TrackJson);
    }

    [Fact]
    public async Task Get_Legacy_MatchesList()
    {
        var storage = new TestStorage(azurite);
        var repo = await storage.CreateRepositoryAsync();
        await storage.TripsTableClient.AddEntityAsync(LegacyEntities.A("trip_20250101000000"), Ct);

        var listed = (await repo.ListAsync()).Single();
        var got = await repo.GetAsync("trip_20250101000000");

        Assert.Equal(listed, got);
    }

    [Fact]
    public async Task Update_LegacyEntities_KeepEveryUnpostedProperty()
    {
        var storage = new TestStorage(azurite);
        var repo = await storage.CreateRepositoryAsync();
        var a = "trip_20250101000000";
        var b = "trip_20240101000000";
        await storage.TripsTableClient.AddEntityAsync(LegacyEntities.A(a), Ct);
        await storage.TripsTableClient.AddEntityAsync(LegacyEntities.B(b), Ct);
        var beforeA = await storage.RawAsync(a);
        var beforeB = await storage.RawAsync(b);

        await repo.UpdateAsync(a, new TripFormData { Title = "A2" });
        await repo.UpdateAsync(b, new TripFormData { Title = "B2" });

        foreach (var (id, before) in new[] { (a, beforeA), (b, beforeB) })
        {
            var after = await storage.RawAsync(id);
            foreach (var (key, value) in before.Where(kv => kv.Key is not ("Title" or "Timestamp" or "odata.etag")))
            {
                Assert.True(after.ContainsKey(key), $"{id}: property {key} was removed");
                Assert.Equal(value, after[key]);
                Assert.Equal(value.GetType(), after[key].GetType());
            }
        }
    }
}
