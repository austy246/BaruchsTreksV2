using System.Globalization;
using System.Security;
using System.Text;

namespace BaruchsTreks.Tracks;

/// <summary>
/// Port of <c>build_gpx</c>: turns plain point arrays (Strava streams) into the same GPX document
/// an uploaded Garmin file arrives as.
/// </summary>
public static class GpxBuilder
{
    public static byte[] Build(string name, IEnumerable<TrackPoint> points, DateTimeOffset? start)
    {
        ArgumentNullException.ThrowIfNull(points);
        var escapedName = SecurityElement.Escape(name) ?? "";

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<gpx xmlns=\"http://www.topografix.com/GPX/1/1\" ")
          .Append("xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" ")
          .Append("xsi:schemaLocation=\"http://www.topografix.com/GPX/1/1 http://www.topografix.com/GPX/1/1/gpx.xsd\" ")
          .Append("version=\"1.1\" creator=\"Baruch's Treks\">\n");
        sb.Append("  <metadata>\n");
        sb.Append("    <name>").Append(escapedName).Append("</name>\n");
        if (start is { } activityTime)
        {
            sb.Append("    <time>").Append(FormatTime(activityTime)).Append("</time>\n");
        }

        sb.Append("  </metadata>\n");
        sb.Append("  <trk>\n");
        sb.Append("    <name>").Append(escapedName).Append("</name>\n");
        sb.Append("    <trkseg>\n");
        foreach (var p in points)
        {
            sb.Append("      <trkpt lat=\"").Append(PyFloat(p.Lat)).Append("\" lon=\"").Append(PyFloat(p.Lng)).Append("\">\n");
            if (p.Ele is { } ele) sb.Append("        <ele>").Append(PyFloat(ele)).Append("</ele>\n");
            if (p.Time is { } time) sb.Append("        <time>").Append(FormatTime(time)).Append("</time>\n");
            sb.Append("      </trkpt>\n");
        }

        sb.Append("    </trkseg>\n");
        sb.Append("  </trk>\n");
        sb.Append("</gpx>");

        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    private static string FormatTime(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Invariant, round-trippable, and "400.0" rather than "400" like Python's repr.</summary>
    private static string PyFloat(double value)
    {
        var s = value.ToString("R", CultureInfo.InvariantCulture);
        return s.AsSpan().IndexOfAny('.', 'E') < 0 ? s + ".0" : s;
    }
}
