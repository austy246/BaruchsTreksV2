using System.Globalization;
using System.Text.Json;
using BaruchsTreks.Trips;

namespace BaruchsTreks.Tracks;

/// <summary>
/// Rozdíl mezi hodnotou z GPX a hodnotou výletu. <see cref="Current"/> je <c>null</c>, když je pole
/// prázdné (vyplní se bez ptaní); jinak jde o odlišnou hodnotu, která vyžaduje potvrzení.
/// </summary>
public sealed record FieldDiff(string Field, string Label, string? Current, string New);

/// <summary>Port <c>derived_from_track</c> a <c>compare_with_track</c> z <c>trips/views.py</c>.</summary>
public static class TrackFieldComparer
{
    // Pole, která GPX umí dodat, v pořadí, v jakém se vypisují k potvrzení.
    private static readonly (string Field, string Label)[] GpxFields =
    [
        ("meters_ascend", "Převýšení nahoru"),
        ("meters_descend", "Převýšení dolů"),
        ("length_hours", "Délka trvání"),
        ("trip_completed_on", "Datum"),
        ("parking_json", "Bod startu"),
        ("high_point_json", "Vrchol"),
    ];

    /// <summary>Hodnoty polí výletu, které uložená trasa dodá. Klíče jsou názvy polí z Django.</summary>
    public static IReadOnlyDictionary<string, object> Derived(TrackStats? stats)
    {
        var derived = new Dictionary<string, object>();
        if (stats is null)
            return derived;

        derived["meters_ascend"] = stats.MetersAscend;
        derived["meters_descend"] = stats.MetersDescend;
        if (stats.DurationHours is { } hours && hours != 0)
            derived["length_hours"] = hours;
        if (!string.IsNullOrEmpty(stats.StartedOn))
            derived["trip_completed_on"] = stats.StartedOn;
        if (stats.Start is not null)
            derived["parking_json"] = JsonSerializer.Serialize(stats.Start);
        if (stats.HighPoint is not null)
            derived["high_point_json"] = JsonSerializer.Serialize(stats.HighPoint);
        return derived;
    }

    public static IReadOnlyList<FieldDiff> Compare(IReadOnlyDictionary<string, object> derived, Trip current)
    {
        var diffs = new List<FieldDiff>();
        foreach (var (field, label) in GpxFields)
        {
            if (!derived.TryGetValue(field, out var newValue))
                continue;

            var existing = CurrentValue(field, current);
            if (IsBlank(existing))
                diffs.Add(new FieldDiff(field, label, null, Display(field, newValue)));
            else if (!SameValue(field, existing, newValue))
                diffs.Add(new FieldDiff(field, label, Display(field, existing), Display(field, newValue)));
        }
        return diffs;
    }

    private static object CurrentValue(string field, Trip trip) => field switch
    {
        "meters_ascend" => MetersValue(trip.MetersAscend),
        "meters_descend" => MetersValue(trip.MetersDescend),
        "length_hours" => trip.LengthHoursIsInteger ? (object)(long)trip.LengthHours : trip.LengthHours,
        "trip_completed_on" => trip.TripCompletedOn,
        "parking_json" => trip.ParkingJson,
        "high_point_json" => trip.HighPointJson,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    // Metry jsou v Django celá čísla (IntegerField); Trip je drží jako double kvůli smíšeným typům v datech.
    private static object MetersValue(double meters) =>
        meters == Math.Floor(meters) && Math.Abs(meters) < long.MaxValue ? (object)(long)meters : meters;

    // Nula nebo prázdný řetězec (i "none") znamená, že tam zatím nic zadané není.
    private static bool IsBlank(object? value) => value switch
    {
        null => true,
        string s => s.Trim() is "" or "none",
        _ => ToDouble(value) is { } d && d == 0,
    };

    private static bool SameValue(string field, object current, object @new)
    {
        switch (field)
        {
            case "parking_json" or "high_point_json":
                var a = Coordinates(current);
                var b = Coordinates(@new);
                return a is null ? b is null : b is not null && a.Value == b.Value;
            case "trip_completed_on":
                return DateText(current) == DateText(@new);
            default:
                return ToDouble(current) is { } x && ToDouble(@new) is { } y
                    ? Math.Abs(x - y) < 0.01
                    : Text(current) == Text(@new);
        }
    }

    private static string Display(string field, object value)
    {
        switch (field)
        {
            case "parking_json" or "high_point_json":
                if (Coordinates(value) is not { } c)
                    return "—";
                var text = string.Create(CultureInfo.InvariantCulture, $"{c.Lat:F5}, {c.Lng:F5}");
                if (Elevation(value) is { } elevation)
                    text += string.Create(CultureInfo.InvariantCulture, $" ({Math.Round(elevation)} m)");
                return text;
            case "trip_completed_on":
                return DateText(value);
            case "length_hours":
                return $"{Text(value)} h";
            default:
                return $"{Text(value)} m";
        }
    }

    private static (double Lat, double Lng)? Coordinates(object value)
    {
        try
        {
            using var doc = JsonDocument.Parse(value as string ?? "");
            var root = doc.RootElement;
            var lat = Number(root.GetProperty("Latitude"));
            var lng = Number(root.GetProperty("Longtitude"));
            return (Math.Round(lat, 5), Math.Round(lng, 5));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static double? Elevation(object value)
    {
        try
        {
            using var doc = JsonDocument.Parse(value as string ?? "");
            if (doc.RootElement.TryGetProperty("Elevation", out var e) && Number(e) is var n and not 0)
                return n;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Bez nadmořské výšky.
        }
        return null;
    }

    // Číslo nebo číslo v řetězci (Python float() bere obojí).
    private static double Number(JsonElement element) => element.ValueKind == JsonValueKind.String
        ? double.Parse(element.GetString()!, CultureInfo.InvariantCulture)
        : element.GetDouble();

    private static double? ToDouble(object value) => value switch
    {
        double d => d,
        int i => i,
        long l => l,
        float f => f,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    private static string DateText(object value)
    {
        var text = Text(value);
        return text.Length > 10 ? text[..10] : text;
    }

    private static string Text(object value) => value switch
    {
        double d => PythonFloat(d),
        float f => PythonFloat(f),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    // Python repr(float): nejkratší tvar, který se vrátí zpět; celé číslo má ".0".
    private static string PythonFloat(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.IndexOfAny(['.', 'E', 'N', 'I']) < 0 ? text + ".0" : text;
    }
}
