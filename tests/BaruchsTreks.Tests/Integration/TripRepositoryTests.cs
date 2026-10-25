using Azure.Data.Tables;
using BaruchsTreks.Trips;

namespace BaruchsTreks.Tests.Integration;

public class TripRepositoryTests(AzuriteFixture azurite)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Repository_List_SortedByModifiedDescending()
    {
        var storage = new TestStorage(azurite);
        var repo = await storage.CreateRepositoryAsync();
        // Pořadí RowKey je záměrně opačné než pořadí zápisu: rozhoduje Timestamp, ne klíč.
        await storage.TripsTableClient.AddEntityAsync(new TableEntity("Trips", "trip_20260103000000") { ["Title"] = "první zapsaný" }, Ct);
        await Task.Delay(50, Ct);
        await storage.TripsTableClient.AddEntityAsync(new TableEntity("Trips", "trip_20260102000000") { ["Title"] = "druhý zapsaný" }, Ct);
        await Task.Delay(50, Ct);
        await storage.TripsTableClient.AddEntityAsync(new TableEntity("Trips", "trip_20260101000000") { ["Title"] = "třetí zapsaný" }, Ct);
        // Config řádky se ve výpisu výletů nesmí objevit.
        await storage.TripsTableClient.AddEntityAsync(new TableEntity("Config", "strava_tokens_x") { ["Value"] = "{}" }, Ct);

        var list = await repo.ListAsync();

        Assert.Equal(["třetí zapsaný", "druhý zapsaný", "první zapsaný"], list.Select(t => t.Title));
        Assert.All(list, t => Assert.NotNull(t.ModifiedAt));
    }

    [Fact]
    public async Task Repository_Get_UnknownOrStrangeId_ReturnsNull()
    {
        var storage = new TestStorage(azurite);
        var repo = await storage.CreateRepositoryAsync();

        Assert.Null(await repo.GetAsync("does-not-exist"));
        Assert.Null(await repo.GetAsync("a'b"));
        Assert.Null(await repo.GetAsync("a/b"));
        Assert.Null(await repo.GetAsync(""));
    }

    [Fact]
    public async Task UpdateTrip_PreservesUnpostedProperties()
    {
        var storage = new TestStorage(azurite);
        var repo = await storage.CreateRepositoryAsync();
        var id = "trip_20250101000000";
        await storage.TripsTableClient.AddEntityAsync(LegacyEntities.A(id), Ct);

        await repo.UpdateAsync(id, new TripFormData { Title = "Nový název", Description = "" });

        var raw = await storage.RawAsync(id);
        Assert.Equal("Nový název", raw["Title"]);
        Assert.Equal("", raw["Description"]);
        Assert.Equal("https://img.example/a.jpg", raw["ImageUrl"]);
        Assert.Equal("https://mapy.example/a", raw["MapUrl"]);
        Assert.Equal("zůstane", raw["Extra"]);
        Assert.Equal("Tatry", raw["Location"]);
        Assert.Equal("2025-08-15", raw["TripCompletedOn"]);
        Assert.Equal(1200, raw["ElevationGain"]);
        Assert.Equal("""{"Latitude": 49.1, "Longtitude": 20.2}""", raw["ParkingJson"]);
    }

    [Fact]
    public async Task UpdateTrip_NullAndEmptyJsonOrDate_DoNotOverwrite()
    {
        var storage = new TestStorage(azurite);
        var repo = await storage.CreateRepositoryAsync();
        var id = "trip_20250101000000";
        await storage.TripsTableClient.AddEntityAsync(LegacyEntities.B(id), Ct);

        await repo.UpdateAsync(id, new TripFormData { Title = "x", TrackJson = "", ParkingJson = "", TripCompletedOn = null });

        var raw = await storage.RawAsync(id);
        Assert.Equal("""{"distance_km": 12.3}""", raw["TrackJson"]);
        Assert.False(raw.ContainsKey("ParkingJson"));
        Assert.IsType<DateTimeOffset>(raw["TripCompletedOn"]);
    }

    [Fact]
    public async Task UpdateTrip_KeepsExistingPropertyTypes()
    {
        var storage = new TestStorage(azurite);
        var repo = await storage.CreateRepositoryAsync();
        var a = "trip_20250101000000";
        var b = "trip_20250101000001";
        await storage.TripsTableClient.AddEntityAsync(LegacyEntities.A(a), Ct);
        await storage.TripsTableClient.AddEntityAsync(LegacyEntities.B(b), Ct);
        var data = new TripFormData
        {
            LengthHours = 5,
            MetersAscend = 900,
            MetersDescend = 880,
            ElevationGain = 700,
            TripCompletedOn = new DateOnly(2026, 2, 3),
        };

        await repo.UpdateAsync(a, data);
        await repo.UpdateAsync(b, data);

        var ra = await storage.RawAsync(a);
        Assert.IsType<int>(ra["LengthHours"]);          // byl Int32
        Assert.IsType<int>(ra["MetersAscend"]);
        Assert.IsType<int>(ra["MetersDescend"]);        // chyběl: přirozený Int32
        Assert.Equal("2026-02-03", ra["TripCompletedOn"]); // byl string

        var rb = await storage.RawAsync(b);
        Assert.IsType<double>(rb["LengthHours"]);       // byl Double
        Assert.IsType<double>(rb["MetersAscend"]);      // byl Double
        Assert.IsType<long>(rb["MetersDescend"]);       // byl Int64
        Assert.IsType<long>(rb["ElevationGain"]);       // byl Int64
        Assert.IsType<DateTimeOffset>(rb["TripCompletedOn"]); // byl datetime
        Assert.Equal(new DateTimeOffset(2026, 2, 3, 0, 0, 0, TimeSpan.Zero), rb["TripCompletedOn"]);
        Assert.Equal(900.0, rb["MetersAscend"]);
    }

    [Fact]
    public async Task UpdateTrip_FractionIntoIntegerProperty_IsNotTruncated()
    {
        var storage = new TestStorage(azurite);
        var repo = await storage.CreateRepositoryAsync();
        var id = "trip_20250101000000";
        await storage.TripsTableClient.AddEntityAsync(LegacyEntities.A(id), Ct); // LengthHours = Int32 6

        await repo.UpdateAsync(id, new TripFormData { LengthHours = 6.5 });

        Assert.Equal(6.5, (await storage.RawAsync(id))["LengthHours"]);
    }

    [Fact]
    public async Task UpdateTrip_Missing_Throws()
    {
        var storage = new TestStorage(azurite);
        var repo = await storage.CreateRepositoryAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => repo.UpdateAsync("trip_nope", new TripFormData { Title = "x" }));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => repo.UpdateAsync("a'b", new TripFormData { Title = "x" }));
    }

    [Fact]
    public async Task Create_WritesJsonFieldsAsStrings()
    {
        var storage = new TestStorage(azurite);
        var repo = await storage.CreateRepositoryAsync();
        const string parking = """{"Latitude": 49.5, "Longtitude": 20.5}""";

        var id = await repo.CreateAsync(new TripFormData
        {
            Title = "Nový",
            TripCompletedOn = new DateOnly(2026, 4, 5),
            LengthHours = 3,
            MetersAscend = 400,
            ParkingJson = parking,
            HighPointJson = """{"Latitude": 1, "Longtitude": 2}""",
            TrackJson = """{"distance_km": 5}""",
        });

        Assert.Matches(@"^trip_\d{14}$", id);
        var raw = await storage.RawAsync(id);
        Assert.Equal("Trips", raw.PartitionKey);
        Assert.IsType<string>(raw["ParkingJson"]);
        Assert.Equal(parking, raw["ParkingJson"]);
        Assert.IsType<string>(raw["HighPointJson"]);
        Assert.IsType<string>(raw["TrackJson"]);
        Assert.Equal("2026-04-05", raw["TripCompletedOn"]);
        Assert.IsType<double>(raw["LengthHours"]);
        Assert.IsType<int>(raw["MetersAscend"]);
        Assert.False(raw.ContainsKey("MetersDescend"));   // None = vlastnost se nezapíše
        Assert.False(raw.ContainsKey("ElevationGain"));
        Assert.Equal("", raw["UiaaGrade"]);
        Assert.Equal("", raw["Location"]);

        var trip = await repo.GetAsync(id);
        Assert.Equal("Nový", trip!.Title);
        Assert.Equal("none", trip.UiaaGrade);
        Assert.Equal(parking, trip.ParkingJson);
    }

    [Fact]
    public async Task Create_WithoutDate_OmitsTripCompletedOn_AndTwoCreatesInOneSecondGetDistinctKeys()
    {
        var storage = new TestStorage(azurite);
        var repo = await storage.CreateRepositoryAsync();

        var ids = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => repo.CreateAsync(new TripFormData { Title = "x" })));

        Assert.Equal(4, ids.Distinct().Count());
        Assert.False((await storage.RawAsync(ids[0])).ContainsKey("TripCompletedOn"));
    }

    [Fact]
    public async Task MergeFields_ClearsOneProperty_AndKeepsTheRest()
    {
        var storage = new TestStorage(azurite);
        var repo = await storage.CreateRepositoryAsync();
        var id = "trip_20250101000000";
        await storage.TripsTableClient.AddEntityAsync(LegacyEntities.B(id), Ct);

        await repo.MergeFieldsAsync(id, new Dictionary<string, object?> { ["TrackJson"] = "", ["Skipped"] = null });

        var raw = await storage.RawAsync(id);
        Assert.Equal("", raw["TrackJson"]);
        Assert.False(raw.ContainsKey("Skipped"));
        Assert.Equal("IV", raw["UiaaGrade"]);
        Assert.Equal(4.5, raw["LengthHours"]);
    }

    [Fact]
    public async Task MergeFields_Number_KeepsExistingType()
    {
        var storage = new TestStorage(azurite);
        var repo = await storage.CreateRepositoryAsync();
        var id = "trip_20250101000000";
        await storage.TripsTableClient.AddEntityAsync(LegacyEntities.A(id), Ct);

        await repo.MergeFieldsAsync(id, new Dictionary<string, object?> { ["MetersAscend"] = 1300.0 });

        Assert.IsType<int>((await storage.RawAsync(id))["MetersAscend"]);
    }

    [Fact]
    public async Task Delete_RemovesTrip_AndIsIdempotent()
    {
        var storage = new TestStorage(azurite);
        var repo = await storage.CreateRepositoryAsync();
        var id = await repo.CreateAsync(new TripFormData { Title = "x" });

        await repo.DeleteAsync(id);
        await repo.DeleteAsync(id);
        await repo.DeleteAsync("a'b");

        Assert.Null(await repo.GetAsync(id));
    }

    [Fact]
    public async Task ConfigRepository_SaveGetDelete_StoresJsonInValue_AndStaysOutOfTripList()
    {
        var storage = new TestStorage(azurite);
        var config = await storage.CreateConfigRepositoryAsync();
        var trips = await storage.CreateRepositoryAsync();

        Assert.Null(await config.GetAsync("strava_tokens_jan"));

        await config.SaveAsync("strava_tokens_jan", """{"access_token":"a"}""");
        await config.SaveAsync("strava_tokens_jan", """{"access_token":"b"}""");

        Assert.Equal("""{"access_token":"b"}""", await config.GetAsync("strava_tokens_jan"));
        var raw = (await storage.TripsTableClient.GetEntityAsync<TableEntity>("Config", "strava_tokens_jan", cancellationToken: Ct)).Value;
        Assert.Equal("""{"access_token":"b"}""", raw["Value"]);
        Assert.Empty(await trips.ListAsync());

        await config.DeleteAsync("strava_tokens_jan");
        await config.DeleteAsync("strava_tokens_jan");
        Assert.Null(await config.GetAsync("strava_tokens_jan"));
    }
}
