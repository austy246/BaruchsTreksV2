using System.Globalization;
using BaruchsTreks.Tracks;
using BaruchsTreks.Trips;

namespace BaruchsTreks.Tests.Unit;

public class TrackFieldComparerTests
{
    private static TrackStats Stats() => new()
    {
        MetersAscend = 500,
        MetersDescend = 480,
        DurationHours = 4.5,
        StartedOn = "2024-05-01",
        Start = new StartPoint(49.1, 17.2),
        HighPoint = new HighPoint(49.2, 17.3, 1234.4),
    };

    [Fact]
    public void Derived_NullOrEmptyStats_GivesNothing()
    {
        Assert.Empty(TrackFieldComparer.Derived(null));

        var derived = TrackFieldComparer.Derived(new TrackStats { DurationHours = 0 });
        Assert.Equal(["meters_ascend", "meters_descend"], derived.Keys);
    }

    [Fact]
    public void Derived_KeepsLongtitudeTypoInJson()
    {
        var derived = TrackFieldComparer.Derived(Stats());

        Assert.Equal(500, derived["meters_ascend"]);
        Assert.Equal(4.5, derived["length_hours"]);
        Assert.Equal("2024-05-01", derived["trip_completed_on"]);
        Assert.Contains("\"Longtitude\":17.2", (string)derived["parking_json"]);
        Assert.Contains("\"Elevation\":1234.4", (string)derived["high_point_json"]);
    }

    [Fact]
    public void Compare_BlankFieldsAreFilled()
    {
        var diffs = TrackFieldComparer.Compare(TrackFieldComparer.Derived(Stats()), new Trip { TripCompletedOn = "", ParkingJson = "none" });

        Assert.Equal(
            ["Převýšení nahoru", "Převýšení dolů", "Délka trvání", "Datum", "Bod startu", "Vrchol"],
            diffs.Select(d => d.Label));
        Assert.All(diffs, d => Assert.Null(d.Current));
        Assert.Equal("500 m", diffs[0].New);
        Assert.Equal("4.5 h", diffs[2].New);
        Assert.Equal("2024-05-01", diffs[3].New);
        Assert.Equal("49.10000, 17.20000", diffs[4].New);
        Assert.Equal("49.20000, 17.30000 (1234 m)", diffs[5].New);
    }

    [Fact]
    public void Compare_EqualValuesAreNotConflicts()
    {
        var trip = new Trip
        {
            MetersAscend = 500.004,
            MetersDescend = 480,
            LengthHours = 4.5,
            TripCompletedOn = "2024-05-01",
            ParkingJson = "{\"Latitude\": 49.100001, \"Longtitude\": 17.2}",
            HighPointJson = "{\"Latitude\":49.2,\"Longtitude\":17.3,\"Elevation\":900}",
        };

        Assert.Empty(TrackFieldComparer.Compare(TrackFieldComparer.Derived(Stats()), trip));
    }

    [Fact]
    public void Compare_DifferentValuesAreListed()
    {
        var trip = new Trip
        {
            MetersAscend = 300,
            MetersDescend = 0,
            LengthHours = 3,
            TripCompletedOn = "2023-01-02",
            ParkingJson = "{\"Latitude\":50,\"Longtitude\":18}",
            HighPointJson = "",
        };

        var diffs = TrackFieldComparer.Compare(TrackFieldComparer.Derived(Stats()), trip);

        Assert.Equal(["meters_ascend", "meters_descend", "length_hours", "trip_completed_on", "parking_json", "high_point_json"], diffs.Select(d => d.Field));
        Assert.Equal("300 m", diffs[0].Current);
        Assert.Equal("500 m", diffs[0].New);
        Assert.Null(diffs[1].Current);
        Assert.Equal("3.0 h", diffs[2].Current);
        Assert.Equal("2023-01-02", diffs[3].Current);
        Assert.Equal("50.00000, 18.00000", diffs[4].Current);
        Assert.Null(diffs[5].Current);
    }

    [Fact]
    public void Compare_OnlyFieldsThatTrackSupplies()
    {
        var derived = new Dictionary<string, object> { ["meters_ascend"] = 100 };

        var diffs = TrackFieldComparer.Compare(derived, new Trip());

        Assert.Single(diffs);
        Assert.Equal("meters_ascend", diffs[0].Field);
    }

    private static string NewText(string field, object value) =>
        TrackFieldComparer.Compare(new Dictionary<string, object> { [field] = value }, new Trip())[0].New;

    [Theory]
    [InlineData(3.0, "3.0 h")]
    [InlineData(2.5, "2.5 h")]
    [InlineData(0.35, "0.35 h")]
    public void Display_Hours_UsesPythonFloatStyle(double hours, string expected) =>
        Assert.Equal(expected, NewText("length_hours", hours));

    [Fact]
    public void Display_Meters_FollowsRuntimeType()
    {
        Assert.Equal("300 m", NewText("meters_ascend", 300));
        Assert.Equal("300 m", NewText("meters_ascend", 300L));
        Assert.Equal("300.0 m", NewText("meters_ascend", 300.0));
    }

    [Fact]
    public void Display_CurrentWholeMeters_ArePlainIntegers()
    {
        var diffs = TrackFieldComparer.Compare(new Dictionary<string, object> { ["meters_ascend"] = 500 }, new Trip { MetersAscend = 300 });

        Assert.Equal("300 m", diffs[0].Current);
    }

    [Fact]
    public void Display_UnderCzechCulture_UsesDots()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("cs-CZ");
        try
        {
            Assert.Equal("2.5 h", NewText("length_hours", 2.5));
            Assert.Equal("49.10000, 17.20000", NewText("parking_json", "{\"Latitude\":49.1,\"Longtitude\":17.2}"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Display_UnparsableCoordinates_ShowsDash()
    {
        var diffs = TrackFieldComparer.Compare(
            new Dictionary<string, object> { ["parking_json"] = "{\"Latitude\":49.1,\"Longtitude\":17.2}" },
            new Trip { ParkingJson = "garbage" });

        Assert.Equal("—", diffs[0].Current);
    }
}
