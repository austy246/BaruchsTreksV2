using System.Net;
using Azure.Data.Tables;
using BaruchsTreks.Tracks;
using BaruchsTreks.Trips;

namespace BaruchsTreks.Tests.Integration;

/// <summary>Django vypisuje LengthHours podle uloženého typu (Int32 "16", Double "3.0"); .NET to musí zachovat.</summary>
public class LengthHoursTypeTests(AzuriteFixture azurite)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TableEntity Entity(string id, string title, object hours) => new("Trips", id)
    {
        ["Title"] = title,
        ["LengthHours"] = hours,
    };

    private static async Task SeedAsync(AppFactory factory)
    {
        await factory.PutRawTripAsync(Entity("trip_20250101000001", "IntTrip", 16));
        await factory.PutRawTripAsync(Entity("trip_20250101000002", "DoubleTrip", 3.0));
        await factory.PutRawTripAsync(Entity("trip_20250101000003", "LongTrip", 8L));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("cs-CZ")]
    public async Task DetailAndList_PreserveStoredType(string culture)
    {
        await using var factory = new AppFactory(azurite).WithCulture(culture);
        await SeedAsync(factory);
        var client = factory.CreateClient();

        var intDetail = await client.GetStringAsync("/trips/trip/trip_20250101000001/", Ct);
        var doubleDetail = await client.GetStringAsync("/trips/trip/trip_20250101000002/", Ct);
        var longDetail = await client.GetStringAsync("/trips/trip/trip_20250101000003/", Ct);
        var all = await client.GetStringAsync("/trips/all/", Ct);
        var index = await client.GetStringAsync("/trips/", Ct);

        Assert.Contains("16 hours", intDetail);
        Assert.DoesNotContain("16.0 hours", intDetail);
        Assert.Contains("3.0 hours", doubleDetail);
        Assert.Contains("8 hours", longDetail);
        Assert.DoesNotContain("8.0 hours", longDetail);
        foreach (var page in new[] { all, index })
        {
            Assert.Contains("16 hours", page);
            Assert.DoesNotContain("16.0 hours", page);
            Assert.Contains("3.0 hours", page);
            Assert.Contains("8 hours", page);
            Assert.DoesNotContain("8.0 hours", page);
        }
    }

    [Fact]
    public async Task EditForm_InitialValueFollowsStoredType()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        await SeedAsync(factory);

        var intEdit = WebUtility.HtmlDecode(await admin.GetStringAsync("/trips/trip/trip_20250101000001/edit/", Ct));
        var doubleEdit = WebUtility.HtmlDecode(await admin.GetStringAsync("/trips/trip/trip_20250101000002/edit/", Ct));
        var longEdit = WebUtility.HtmlDecode(await admin.GetStringAsync("/trips/trip/trip_20250101000003/edit/", Ct));

        Assert.Matches("name=\"length_hours\"[^>]*value=\"16\"", intEdit);
        Assert.Matches("name=\"length_hours\"[^>]*value=\"3[.]0\"", doubleEdit);
        Assert.Matches("name=\"length_hours\"[^>]*value=\"8\"", longEdit);
    }

    [Fact]
    public async Task Resave_KeepsIntegerTypeForWholeValueAndBecomesDoubleForFraction()
    {
        await using var factory = new AppFactory(azurite);
        var admin = await factory.CreateUserClientAsync(isAdmin: true);
        await SeedAsync(factory);

        await admin.PostFormAsync("/trips/trip/trip_20250101000001/edit/", "/trips/trip/trip_20250101000001/edit/", [("title", "IntTrip"), ("length_hours", "16")]);
        Assert.Equal(16, Assert.IsType<int>((await factory.RawTripAsync("trip_20250101000001"))["LengthHours"]));

        await admin.PostFormAsync("/trips/trip/trip_20250101000001/edit/", "/trips/trip/trip_20250101000001/edit/", [("title", "IntTrip"), ("length_hours", "16.5")]);
        Assert.Equal(16.5, Assert.IsType<double>((await factory.RawTripAsync("trip_20250101000001"))["LengthHours"]));
    }

    [Fact]
    public void Mapper_FlagsOnlyIntegralStoredTypes()
    {
        Assert.True(TripEntityMapper.FromEntity(Entity("trip_1", "a", 16)).LengthHoursIsInteger);
        Assert.True(TripEntityMapper.FromEntity(Entity("trip_1", "a", 16L)).LengthHoursIsInteger);
        Assert.False(TripEntityMapper.FromEntity(Entity("trip_1", "a", 3.0)).LengthHoursIsInteger);
        Assert.False(TripEntityMapper.FromEntity(Entity("trip_1", "a", "16")).LengthHoursIsInteger);
        Assert.False(TripEntityMapper.FromEntity(new TableEntity("Trips", "trip_1")).LengthHoursIsInteger);
    }

    [Fact]
    public void Comparer_ShowsIntegralCurrentValueWithoutDecimal()
    {
        var stats = new TrackStats { DurationHours = 4.5 };
        var derived = TrackFieldComparer.Derived(stats);

        var intDiff = TrackFieldComparer.Compare(derived, new Trip { LengthHours = 16, LengthHoursIsInteger = true })
            .Single(d => d.Field == "length_hours");
        var doubleDiff = TrackFieldComparer.Compare(derived, new Trip { LengthHours = 3 })
            .Single(d => d.Field == "length_hours");

        Assert.Equal("16 h", intDiff.Current);
        Assert.Equal("3.0 h", doubleDiff.Current);
    }
}
