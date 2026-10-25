using System.Globalization;
using System.Text;
using System.Text.Json;
using BaruchsTreks.Tracks;

namespace BaruchsTreks.Tests.Unit;

public class GpxParserTests
{
    private const double Tolerance = 1e-6;
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "gpx");

    public static TheoryData<string> FixtureNames()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(FixtureDir, "*.expected.json").Order())
        {
            data.Add(Path.GetFileName(file)[..^".expected.json".Length]);
        }

        return data;
    }

    [Fact]
    public void Fixtures_AreDeployedWithTheTests() => Assert.True(FixtureNames().Count() >= 15);

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Parse_MatchesGolden(string name)
    {
        var raw = File.ReadAllBytes(Path.Combine(FixtureDir, name + ".gpx"));
        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir, name + ".expected.json")));

        var parsed = GpxParser.Parse(raw);

        using var actualTrackJson = JsonDocument.Parse(parsed.Stats.ToJson());
        using var actualGeoJson = JsonDocument.Parse(parsed.GeoJson);
        AssertJsonEqual(expected.RootElement.GetProperty("track_json"), actualTrackJson.RootElement, "track_json");
        AssertJsonEqual(expected.RootElement.GetProperty("geojson"), actualGeoJson.RootElement, "geojson");
    }

    [Fact]
    public void Parse_NonGpx_Throws()
    {
        Assert.Throws<GpxParseError>(() => GpxParser.Parse("this is not a gpx file"u8.ToArray()));
        Assert.Throws<GpxParseError>(() => GpxParser.Parse("<html><body/></html>"u8.ToArray()));
        Assert.Throws<GpxParseError>(() => GpxParser.Parse([]));
    }

    [Fact]
    public void Parse_DtdIsRejected()
    {
        var xxe = "<?xml version=\"1.0\"?><!DOCTYPE gpx [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><gpx>&x;</gpx>";
        Assert.Throws<GpxParseError>(() => GpxParser.Parse(Encoding.UTF8.GetBytes(xxe)));
    }

    [Fact]
    public void Parse_NoPoints_Throws()
    {
        var empty = "<?xml version=\"1.0\"?><gpx version=\"1.1\" creator=\"t\" xmlns=\"http://www.topografix.com/GPX/1/1\"></gpx>";
        Assert.Throws<GpxParseError>(() => GpxParser.Parse(Encoding.UTF8.GetBytes(empty)));

        var emptyTrack = "<gpx><trk><trkseg/></trk><rte/></gpx>";
        Assert.Throws<GpxParseError>(() => GpxParser.Parse(Encoding.UTF8.GetBytes(emptyTrack)));
    }

    [Theory]
    [InlineData("<gpx><trk><trkseg><trkpt lat=\"abc\" lon=\"14\"/></trkseg></trk></gpx>")]
    [InlineData("<gpx><trk><trkseg><trkpt lat=\"50\"/></trkseg></trk></gpx>")]
    [InlineData("<gpx><trk><trkseg><trkpt lat=\"95\" lon=\"14\"/></trkseg></trk></gpx>")]
    [InlineData("<gpx><trk><trkseg><trkpt lat=\"50\" lon=\"14\"><ele>x</ele></trkpt></trkseg></trk></gpx>")]
    [InlineData("<gpx><trk><trkseg><trkpt lat=\"50\" lon=\"14\"><time>yesterday-ish</time></trkpt></trkseg></trk></gpx>")]
    [InlineData("<gpx><trk><trkseg><trkpt lat=\"50\" lon=\"14\"></trkseg></trk></gpx>")]
    public void Parse_MalformedPoints_ThrowGpxParseErrorNotOtherExceptions(string xml)
    {
        Assert.Throws<GpxParseError>(() => GpxParser.Parse(Encoding.UTF8.GetBytes(xml)));
    }

    [Fact]
    public void Parse_Latin1_Decodes()
    {
        var raw = File.ReadAllBytes(Path.Combine(FixtureDir, "latin1.gpx"));
        Assert.Contains((byte)0xE9, raw); // really not UTF-8
        Assert.Throws<DecoderFallbackException>(() => new UTF8Encoding(false, true).GetString(raw));

        Assert.Equal("Café Müller", GpxParser.Parse(raw).Stats.Name);
    }

    [Fact]
    public void Parse_Utf8Bom_IsStripped()
    {
        var raw = File.ReadAllBytes(Path.Combine(FixtureDir, "utf8_bom.gpx"));
        Assert.Equal(0xEF, raw[0]);

        Assert.Equal("Bom", GpxParser.Parse(raw).Stats.Name);
    }

    [Fact]
    public void Parse_UnderCzechCulture_UsesDots()
    {
        var previous = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        try
        {
            var cs = new CultureInfo("cs-CZ");
            Assert.Equal(",", cs.NumberFormat.NumberDecimalSeparator); // the test is meaningful
            CultureInfo.CurrentCulture = cs;
            CultureInfo.CurrentUICulture = cs;

            var parsed = GpxParser.Parse(File.ReadAllBytes(Path.Combine(FixtureDir, "hike_time_ele.gpx")));
            var trackJson = parsed.Stats.ToJson();

            Assert.DoesNotMatch(@"""distance_km"":\d+,", trackJson); // would be a comma decimal
            Assert.Matches(@"""distance_km"":\d+\.\d+", trackJson);
            Assert.Matches(@"""Latitude"":50\.7\d+", trackJson);
            Assert.Matches(@"\[15\.7\d+,50\.7\d+,\d+\.\d\]", parsed.GeoJson);

            // The numbers still parse correctly on the other side.
            using var doc = JsonDocument.Parse(parsed.GeoJson);
            Assert.InRange(
                doc.RootElement.GetProperty("geometry").GetProperty("coordinates")[0][1].GetDouble(), 50.7, 50.8);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }

    [Fact]
    public void Simplify_CapsAt2000Points()
    {
        // A zigzag wider than the 5 m tolerance: nothing simplifies away, so the cap must.
        var sb = new StringBuilder("<gpx><trk><trkseg>");
        for (var i = 0; i < 5000; i++)
        {
            var lat = (50.0 + i * 0.00002).ToString("F6", CultureInfo.InvariantCulture);
            var lng = (14.0 + (i % 2 == 0 ? 0.0 : 0.0003)).ToString("F6", CultureInfo.InvariantCulture);
            sb.Append($"<trkpt lat=\"{lat}\" lon=\"{lng}\"/>");
        }

        sb.Append("</trkseg></trk></gpx>");

        var parsed = GpxParser.Parse(Encoding.UTF8.GetBytes(sb.ToString()));

        Assert.Equal(5000, parsed.Stats.OriginalPointCount);
        Assert.Equal(2000, parsed.Stats.PointCount);
        Assert.Equal(2000, parsed.Points.Count);
        Assert.Equal(50.0, parsed.Points[0].Lat);
        Assert.Equal(50.0 + 4999 * 0.00002, parsed.Points[^1].Lat, 6); // last point is always kept
    }

    [Fact]
    public void Parse_ReturnsRoundedSimplifiedPoints()
    {
        var parsed = GpxParser.Parse(File.ReadAllBytes(Path.Combine(FixtureDir, "turn.gpx")));

        Assert.Equal(4, parsed.Points.Count);
        Assert.Equal(new TrackPoint(50.0, 14.0, 400.0, parsed.Points[0].Time), parsed.Points[0]);
        Assert.NotNull(parsed.Points[0].Time);
    }

    [Fact]
    public void Parse_TimeWithOffset_StartedOnIsTheWrittenLocalDate()
    {
        var raw = File.ReadAllBytes(Path.Combine(FixtureDir, "metadata_name_offset_time.gpx"));

        var stats = GpxParser.Parse(raw).Stats;

        Assert.Equal("2025-07-13", stats.StartedOn); // 22:30Z on the 12th, written as 00:30+02:00
        Assert.Equal("Metadata jmeno", stats.Name);
    }

    [Fact]
    public void TrackStats_RoundTripsThroughJson()
    {
        var stats = GpxParser.Parse(File.ReadAllBytes(Path.Combine(FixtureDir, "hike_time_ele.gpx"))).Stats;

        Assert.Equal(stats.ToJson(), TrackStats.FromJson(stats.ToJson())!.ToJson());
    }

    [Fact]
    public void TrackJson_OmitsHighPointAndStartedOnWhenAbsent_ButKeepsNullFields()
    {
        var stats = GpxParser.Parse(File.ReadAllBytes(Path.Combine(FixtureDir, "track_no_ele.gpx"))).Stats;
        using var doc = JsonDocument.Parse(stats.ToJson());
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("high_point", out _));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("min_elevation").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("max_elevation").ValueKind);
        Assert.Equal("Longtitude", root.GetProperty("start").EnumerateObject().Last().Name);
    }

    private static void AssertJsonEqual(JsonElement expected, JsonElement actual, string path)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                Assert.True(actual.ValueKind == JsonValueKind.Object, $"{path}: expected object, got {actual.ValueKind}");
                var expectedNames = expected.EnumerateObject().Select(p => p.Name).Order().ToList();
                var actualNames = actual.EnumerateObject().Select(p => p.Name).Order().ToList();
                Assert.True(expectedNames.SequenceEqual(actualNames),
                    $"{path}: keys differ. expected [{string.Join(",", expectedNames)}] actual [{string.Join(",", actualNames)}]");
                foreach (var prop in expected.EnumerateObject())
                {
                    AssertJsonEqual(prop.Value, actual.GetProperty(prop.Name), $"{path}.{prop.Name}");
                }

                break;
            case JsonValueKind.Array:
                Assert.True(actual.ValueKind == JsonValueKind.Array, $"{path}: expected array, got {actual.ValueKind}");
                Assert.True(expected.GetArrayLength() == actual.GetArrayLength(),
                    $"{path}: length expected {expected.GetArrayLength()} actual {actual.GetArrayLength()}");
                for (var i = 0; i < expected.GetArrayLength(); i++)
                {
                    AssertJsonEqual(expected[i], actual[i], $"{path}[{i}]");
                }

                break;
            case JsonValueKind.Number:
                Assert.True(actual.ValueKind == JsonValueKind.Number, $"{path}: expected number, got {actual.ValueKind}");
                var diff = Math.Abs(expected.GetDouble() - actual.GetDouble());
                Assert.True(diff <= Tolerance, $"{path}: expected {expected.GetDouble():R} actual {actual.GetDouble():R}");
                break;
            default:
                Assert.True(expected.ValueKind == actual.ValueKind, $"{path}: expected {expected.ValueKind} got {actual.ValueKind}");
                if (expected.ValueKind == JsonValueKind.String)
                {
                    Assert.Equal(expected.GetString(), actual.GetString());
                }

                break;
        }
    }
}
