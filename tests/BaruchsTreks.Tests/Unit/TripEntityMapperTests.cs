using Azure.Data.Tables;
using BaruchsTreks.Trips;

namespace BaruchsTreks.Tests.Unit;

public class TripEntityMapperTests
{
    [Fact]
    public void Mapper_ReadsInt32Int64DoubleAndStrings()
    {
        var e = new TableEntity("Trips", "trip_20260102030405")
        {
            ["Title"] = "T",
            ["LengthHours"] = 3,
            ["ElevationGain"] = 1500L,
            ["MetersAscend"] = 12.5,
            ["MetersDescend"] = "130",
            ["Location"] = 42,
        };

        var t = TripEntityMapper.FromEntity(e);

        Assert.Equal("trip_20260102030405", t.Id);
        Assert.Equal(3.0, t.LengthHours);
        Assert.Equal(1500.0, t.ElevationGain);
        Assert.Equal(12.5, t.MetersAscend);
        Assert.Equal(130.0, t.MetersDescend);
        Assert.Equal("42", t.Location);
    }

    [Fact]
    public void Mapper_MissingFields_UseDefaults()
    {
        var t = TripEntityMapper.FromEntity(new TableEntity("Trips", "trip_20260102030405"));

        Assert.Equal("Untitled Trip", t.Title);
        Assert.Equal("", t.Description);
        Assert.Equal("", t.TripCompletedOn);
        Assert.Null(t.CompletedAt);
        Assert.Equal(0, t.LengthHours);
        Assert.Equal(0, t.ElevationGain);
        Assert.Equal(0, t.MetersAscend);
        Assert.Equal(0, t.MetersDescend);
        Assert.Equal("", t.ParkingJson);
        Assert.Equal("", t.TrackJson);
        Assert.Equal("none", t.UiaaGrade);
        Assert.Equal("none", t.AlpineGrade);
        Assert.Equal("none", t.TripClass);
        Assert.Equal("none", t.FerataGrade);
        Assert.Null(t.ModifiedAt);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), t.RowKeyTime);
    }

    [Fact]
    public void Mapper_EmptyOrNonsenseValues_DoNotThrow()
    {
        var e = new TableEntity("Trips", "custom-key")
        {
            ["LengthHours"] = "",
            ["ElevationGain"] = "abc",
            ["MetersAscend"] = double.NaN,
            ["TripCompletedOn"] = "not a date",
            ["UiaaGrade"] = "",
            ["TripClass"] = "Run",
        };

        var t = TripEntityMapper.FromEntity(e);

        Assert.Equal(0, t.LengthHours);
        Assert.Equal(0, t.ElevationGain);
        Assert.Equal(0, t.MetersAscend);
        Assert.Equal("not a date", t.TripCompletedOn);
        Assert.Null(t.CompletedAt);
        Assert.Null(t.RowKeyTime);
        Assert.Equal("none", t.UiaaGrade);
        Assert.Equal("Run", t.TripClass);
    }

    [Theory]
    [InlineData("2025-08-15", 2025, 8, 15)]
    [InlineData("2025-08-15T10:30:00", 2025, 8, 15)]
    public void Mapper_TripCompletedOn_String(string raw, int y, int m, int d)
    {
        var t = TripEntityMapper.FromEntity(new TableEntity("Trips", "r") { ["TripCompletedOn"] = raw });

        Assert.Equal(raw, t.TripCompletedOn);
        Assert.Equal(y, t.CompletedAt!.Value.Year);
        Assert.Equal(m, t.CompletedAt.Value.Month);
        Assert.Equal(d, t.CompletedAt.Value.Day);
        Assert.Equal(TimeSpan.Zero, t.CompletedAt.Value.Offset);
    }

    [Fact]
    public void Mapper_TripCompletedOn_DateTimeAndString()
    {
        var asDate = TripEntityMapper.FromEntity(new TableEntity("Trips", "r")
        {
            ["TripCompletedOn"] = new DateTimeOffset(2024, 7, 1, 0, 0, 0, TimeSpan.Zero),
        });
        var asString = TripEntityMapper.FromEntity(new TableEntity("Trips", "r") { ["TripCompletedOn"] = "2024-07-01" });

        Assert.Equal("2024-07-01", asDate.TripCompletedOn);
        Assert.Equal("2024-07-01", asString.TripCompletedOn);
        Assert.Equal(asString.CompletedAt, asDate.CompletedAt);
    }

    [Fact]
    public void Mapper_KeepsJsonStringsVerbatim_IncludingLongtitudeTypo()
    {
        const string json = """{"Latitude": 49.1, "Longtitude": 20.2}""";
        var t = TripEntityMapper.FromEntity(new TableEntity("Trips", "r") { ["ParkingJson"] = json });

        Assert.Equal(json, t.ParkingJson);
    }

    [Fact]
    public void RowKey_RoundTrip()
    {
        var key = TripEntityMapper.NewRowKey(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(2)));

        Assert.Equal("trip_20260304030607", key);
        Assert.Null(TripEntityMapper.ParseRowKeyTime("trip_garbage"));
        Assert.Null(TripEntityMapper.ParseRowKeyTime("other_20260304030607"));
    }
}
