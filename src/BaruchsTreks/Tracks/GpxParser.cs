using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace BaruchsTreks.Tracks;

/// <summary>Raised when a file cannot be read as GPX. The message is shown to the user.</summary>
public sealed class GpxParseError : Exception
{
    public GpxParseError(string message) : base(message) { }
    public GpxParseError(string message, Exception inner) : base(message, inner) { }
}

public sealed record ParsedTrack(TrackStats Stats, string GeoJson, IReadOnlyList<TrackPoint> Points);

/// <summary>
/// Port of <c>trips/gpx_service.py</c> parsing: tracks (or routes) to simplified geometry plus
/// statistics. Reads the XML namespace-agnostically so GPX 1.0, 1.1 and namespace-less files work.
/// </summary>
public static class GpxParser
{
    public const double SimplifyToleranceM = 5.0;
    public const int MaxTrackPoints = 2000;
    public const double ElevationThresholdM = 3.0;
    public const double EarthRadiusM = 6371000.0;

    public static ParsedTrack Parse(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var text = Decode(raw);
        var (points, rawName) = ReadDocument(text);
        if (points.Count == 0)
        {
            throw new GpxParseError("GPX soubor neobsahuje žádné body trasy.");
        }

        var distanceM = TotalDistanceM(points);
        var (ascend, descend) = ElevationGain(points);

        double? durationHours = null;
        DateTimeOffset? startedAt = null;
        var times = points.Where(p => p.Time.HasValue).Select(p => p.Time!.Value).ToList();
        if (times.Count > 0)
        {
            var min = times.MinBy(t => t.UtcDateTime);
            var max = times.Max(t => t.UtcDateTime);
            startedAt = min;
            durationHours = Math.Round((max - min.UtcDateTime).TotalSeconds / 3600.0, 2);
        }

        var withEle = points.Where(p => p.Ele.HasValue).ToList();
        HighPoint? highPoint = null;
        if (withEle.Count > 0)
        {
            var peak = withEle[0];
            foreach (var p in withEle)
            {
                if (p.Ele!.Value > peak.Ele!.Value) peak = p;
            }
            highPoint = new HighPoint(peak.Lat, peak.Lng, Math.Round(peak.Ele!.Value, 1));
        }

        var simplified = CapPointCount(Simplify(points));

        var stats = new TrackStats
        {
            DistanceKm = Math.Round(distanceM / 1000.0, 2),
            MetersAscend = (int)Math.Round(ascend),
            MetersDescend = (int)Math.Round(descend),
            DurationHours = durationHours,
            PointCount = simplified.Count,
            OriginalPointCount = points.Count,
            MinElevation = withEle.Count > 0 ? (int)Math.Round(withEle.Min(p => p.Ele!.Value)) : null,
            MaxElevation = withEle.Count > 0 ? (int)Math.Round(withEle.Max(p => p.Ele!.Value)) : null,
            Bounds =
            [
                [simplified.Min(p => p.Lat), simplified.Min(p => p.Lng)],
                [simplified.Max(p => p.Lat), simplified.Max(p => p.Lng)],
            ],
            Name = rawName,
            Start = new StartPoint(points[0].Lat, points[0].Lng),
            HighPoint = highPoint,
            StartedOn = startedAt?.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };

        var rounded = simplified
            .Select(p => new TrackPoint(
                Math.Round(p.Lat, 6),
                Math.Round(p.Lng, 6),
                p.Ele.HasValue ? Math.Round(p.Ele.Value, 1) : null,
                p.Time))
            .ToList();

        return new ParsedTrack(stats, BuildGeoJson(stats, rounded), rounded);
    }

    // ---- decoding and XML -------------------------------------------------------------------

    private static string Decode(byte[] raw)
    {
        // utf-8-sig, utf-8, latin-1 in this order. Latin-1 accepts every byte sequence.
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        string text;
        try
        {
            text = strictUtf8.GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(raw);
        }

        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    private static (List<TrackPoint> Points, string? Name) ReadDocument(string text)
    {
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException e)
        {
            throw new GpxParseError($"Soubor se nepodařilo přečíst jako GPX: {e.Message}", e);
        }

        var root = doc.Root;
        if (root is null || root.Name.LocalName != "gpx")
        {
            throw new GpxParseError("Soubor se nepodařilo přečíst jako GPX: chybí kořenový prvek gpx.");
        }

        var tracks = Children(root, "trk").ToList();
        var points = new List<TrackPoint>();
        foreach (var track in tracks)
        {
            foreach (var segment in Children(track, "trkseg"))
            {
                foreach (var pt in Children(segment, "trkpt")) points.Add(ReadPoint(pt));
            }
        }

        if (points.Count == 0)
        {
            foreach (var route in Children(root, "rte"))
            {
                foreach (var pt in Children(route, "rtept")) points.Add(ReadPoint(pt));
            }
        }

        var name = Text(Children(root, "name").FirstOrDefault())
            ?? Text(Children(root, "metadata").SelectMany(m => Children(m, "name")).FirstOrDefault());
        if (string.IsNullOrEmpty(name) && tracks.Count > 0)
        {
            name = Text(Children(tracks[0], "name").FirstOrDefault());
        }

        return (points, string.IsNullOrEmpty(name) ? null : name);
    }

    private static IEnumerable<XElement> Children(XElement parent, string localName) =>
        parent.Elements().Where(e => e.Name.LocalName == localName);

    private static string? Text(XElement? element)
    {
        var value = element?.Value;
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static TrackPoint ReadPoint(XElement element)
    {
        var lat = ParseDouble(element.Attribute("lat")?.Value, "lat");
        var lng = ParseDouble(element.Attribute("lon")?.Value, "lon");
        if (lat is < -90 or > 90 || lng is < -180 or > 180)
        {
            throw new GpxParseError("Soubor se nepodařilo přečíst jako GPX: souřadnice mimo rozsah.");
        }

        double? ele = null;
        var eleText = Children(element, "ele").FirstOrDefault()?.Value.Trim();
        if (!string.IsNullOrEmpty(eleText)) ele = ParseDouble(eleText, "ele");

        DateTimeOffset? time = null;
        var timeText = Children(element, "time").FirstOrDefault()?.Value.Trim();
        if (!string.IsNullOrEmpty(timeText))
        {
            if (!DateTimeOffset.TryParse(timeText, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsed))
            {
                throw new GpxParseError($"Soubor se nepodařilo přečíst jako GPX: neplatný čas „{timeText}“.");
            }
            time = parsed;
        }

        return new TrackPoint(lat, lng, ele, time);
    }

    private static double ParseDouble(string? value, string what)
    {
        if (value is null
            || !double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            || !double.IsFinite(d))
        {
            throw new GpxParseError($"Soubor se nepodařilo přečíst jako GPX: neplatná hodnota {what}.");
        }

        return d;
    }

    // ---- geometry and statistics ------------------------------------------------------------

    private static double ToRad(double deg) => deg * Math.PI / 180.0;

    internal static double Haversine(double lat1, double lng1, double lat2, double lng2)
    {
        var phi1 = ToRad(lat1);
        var phi2 = ToRad(lat2);
        var dPhi = phi2 - phi1;
        var dLambda = ToRad(lng2 - lng1);
        var a = Math.Pow(Math.Sin(dPhi / 2), 2) + Math.Cos(phi1) * Math.Cos(phi2) * Math.Pow(Math.Sin(dLambda / 2), 2);
        return 2 * EarthRadiusM * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    private static double TotalDistanceM(List<TrackPoint> points)
    {
        var total = 0.0;
        for (var i = 1; i < points.Count; i++)
        {
            total += Haversine(points[i - 1].Lat, points[i - 1].Lng, points[i].Lat, points[i].Lng);
        }

        return total;
    }

    private static (double Ascend, double Descend) ElevationGain(List<TrackPoint> points)
    {
        double ascend = 0, descend = 0;
        double? reference = null;
        foreach (var p in points)
        {
            if (p.Ele is not { } elevation) continue;
            if (reference is null)
            {
                reference = elevation;
                continue;
            }

            var delta = elevation - reference.Value;
            if (delta >= ElevationThresholdM)
            {
                ascend += delta;
                reference = elevation;
            }
            else if (delta <= -ElevationThresholdM)
            {
                descend += -delta;
                reference = elevation;
            }
        }

        return (ascend, descend);
    }

    private static double PerpendicularDistanceM(TrackPoint point, TrackPoint start, TrackPoint end)
    {
        var latScale = Math.Cos(ToRad(start.Lat));
        if (latScale == 0) latScale = 1e-9;
        var px = (point.Lng - start.Lng) * latScale;
        var py = point.Lat - start.Lat;
        var ex = (end.Lng - start.Lng) * latScale;
        var ey = end.Lat - start.Lat;

        var segmentLenSq = ex * ex + ey * ey;
        double closestX, closestY;
        if (segmentLenSq == 0)
        {
            closestX = closestY = 0.0;
        }
        else
        {
            var t = Math.Max(0.0, Math.Min(1.0, (px * ex + py * ey) / segmentLenSq));
            closestX = t * ex;
            closestY = t * ey;
        }

        var dx = px - closestX;
        var dy = py - closestY;
        return Math.Sqrt(dx * dx + dy * dy) * Math.PI / 180 * EarthRadiusM;
    }

    /// <summary>Ramer-Douglas-Peucker, iterative so long tracks cannot blow the stack.</summary>
    internal static List<TrackPoint> Simplify(List<TrackPoint> points, double toleranceM = SimplifyToleranceM)
    {
        if (points.Count < 3) return [.. points];

        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int First, int Last)>();
        stack.Push((0, points.Count - 1));

        while (stack.Count > 0)
        {
            var (first, last) = stack.Pop();
            if (last <= first + 1) continue;
            double maxDistance = 0.0;
            var index = first;
            for (var i = first + 1; i < last; i++)
            {
                var distance = PerpendicularDistanceM(points[i], points[first], points[last]);
                if (distance > maxDistance)
                {
                    maxDistance = distance;
                    index = i;
                }
            }

            if (maxDistance > toleranceM)
            {
                keep[index] = true;
                stack.Push((first, index));
                stack.Push((index, last));
            }
        }

        var result = new List<TrackPoint>();
        for (var i = 0; i < points.Count; i++)
        {
            if (keep[i]) result.Add(points[i]);
        }

        return result;
    }

    /// <summary>Evenly thin an already simplified track that is still too dense.</summary>
    internal static List<TrackPoint> CapPointCount(List<TrackPoint> points, int limit = MaxTrackPoints)
    {
        if (points.Count <= limit) return points;
        var step = points.Count / (double)limit;
        var thinned = new List<TrackPoint>(limit);
        for (var i = 0; i < limit; i++) thinned.Add(points[(int)(i * step)]);
        thinned[^1] = points[^1];
        return thinned;
    }

    // ---- GeoJSON ----------------------------------------------------------------------------

    private static string BuildGeoJson(TrackStats stats, List<TrackPoint> rounded)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("type", "Feature");

            w.WriteStartObject("properties");
            w.WriteNumber("distance_km", stats.DistanceKm);
            w.WriteNumber("meters_ascend", stats.MetersAscend);
            w.WriteNumber("meters_descend", stats.MetersDescend);
            WriteNullableNumber(w, "duration_hours", stats.DurationHours);
            w.WriteNumber("point_count", stats.PointCount);
            w.WriteNumber("original_point_count", stats.OriginalPointCount);
            WriteNullableNumber(w, "min_elevation", stats.MinElevation);
            WriteNullableNumber(w, "max_elevation", stats.MaxElevation);
            w.WriteStartArray("bounds");
            foreach (var corner in stats.Bounds)
            {
                w.WriteStartArray();
                foreach (var v in corner) w.WriteNumberValue(v);
                w.WriteEndArray();
            }

            w.WriteEndArray();
            if (stats.Name is null) w.WriteNull("name"); else w.WriteString("name", stats.Name);
            w.WriteEndObject();

            w.WriteStartObject("geometry");
            w.WriteString("type", "LineString");
            w.WriteStartArray("coordinates");
            foreach (var p in rounded)
            {
                w.WriteStartArray();
                w.WriteNumberValue(p.Lng);
                w.WriteNumberValue(p.Lat);
                if (p.Ele is { } e) w.WriteNumberValue(e);
                w.WriteEndArray();
            }

            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteNullableNumber(Utf8JsonWriter w, string name, double? value)
    {
        if (value is { } v) w.WriteNumber(name, v); else w.WriteNull(name);
    }

    private static void WriteNullableNumber(Utf8JsonWriter w, string name, int? value)
    {
        if (value is { } v) w.WriteNumber(name, v); else w.WriteNull(name);
    }
}
