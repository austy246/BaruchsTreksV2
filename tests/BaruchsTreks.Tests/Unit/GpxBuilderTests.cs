using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using BaruchsTreks.Tracks;

namespace BaruchsTreks.Tests.Unit;

public class GpxBuilderTests
{
    private static readonly DateTimeOffset Start = new(2025, 7, 12, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_MatchesPythonOutput()
    {
        var points = new[]
        {
            new TrackPoint(50.0, 14.0, 400.0, Start),
            new TrackPoint(50.001, 14.0, 500.5, Start.AddSeconds(900)),
            new TrackPoint(50.002, 14.0, null, null),
        };

        var actual = GpxBuilder.Build("Ranní výběh", points, Start);

        var golden = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gpx", "build.expected.xml"));
        Assert.True(XNode.DeepEquals(Load(golden), Load(actual)),
            $"Expected:\n{Encoding.UTF8.GetString(golden)}\nActual:\n{Encoding.UTF8.GetString(actual)}");
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", Encoding.UTF8.GetString(actual));
    }

    [Fact]
    public void Build_ThenParse_RoundTrips()
    {
        // The round trip a Strava import makes: streams -> GPX -> stats.
        var points = new[]
        {
            new TrackPoint(50.000, 14.0, 400, Start),
            new TrackPoint(50.001, 14.0, 500, Start.AddSeconds(900)),
            new TrackPoint(50.002, 14.0, 700, Start.AddSeconds(1800)),
        };

        var stats = GpxParser.Parse(GpxBuilder.Build("Ranní výběh", points, Start)).Stats;

        Assert.Equal(300, stats.MetersAscend);
        Assert.Equal(0.5, stats.DurationHours);
        Assert.Equal(new StartPoint(50.0, 14.0), stats.Start);
        Assert.Equal("Ranní výběh", stats.Name);
        Assert.Equal("2025-07-12", stats.StartedOn);
    }

    [Fact]
    public void Build_PointsWithoutTime_ProduceNoDuration()
    {
        var points = new[] { new TrackPoint(50.0, 14.0, 400, null), new TrackPoint(50.002, 14.0, 600, null) };

        var stats = GpxParser.Parse(GpxBuilder.Build("No time", points, null)).Stats;

        Assert.Null(stats.DurationHours);
        Assert.Equal(200, stats.MetersAscend);
        Assert.Null(stats.StartedOn);
    }

    [Fact]
    public void Build_EscapesNameAndIgnoresCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("cs-CZ");
            var xml = Encoding.UTF8.GetString(GpxBuilder.Build("A & <B> \"q\"", [new TrackPoint(50.25, 14.5, 400.5, Start)], null));

            Assert.Contains("lat=\"50.25\" lon=\"14.5\"", xml);
            Assert.Contains("<ele>400.5</ele>", xml);
            Assert.Equal("A & <B> \"q\"", Load(Encoding.UTF8.GetBytes(xml)).Descendants().First(e => e.Name.LocalName == "name").Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static XDocument Load(byte[] xml)
    {
        using var reader = XmlReader.Create(new MemoryStream(xml));
        return XDocument.Load(reader);
    }
}
