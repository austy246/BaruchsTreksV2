using System.Globalization;
using Azure.Data.Tables;
using BaruchsTreks.Storage;

namespace BaruchsTreks.Trips;

/// <summary>
/// Převod mezi <see cref="Trip"/> a entitou v Azure Table Storage. Čtení je tolerantní (typy čísel,
/// chybějící pole, prázdné řetězce, datum jako string i datetime). Zápis je vždy částečný (merge) a existující
/// vlastnosti si drží svůj typ.
/// </summary>
public static class TripEntityMapper
{
    public const string PartitionKey = "Trips";
    private const string RowKeyPrefix = "trip_";
    private const string RowKeyTimeFormat = "yyyyMMddHHmmss";

    public static Trip FromEntity(TableEntity e)
    {
        var completedRaw = e.TryGetValue("TripCompletedOn", out var c) ? c : null;
        var completed = CompletedOnText(completedRaw);

        return new Trip
        {
            Id = e.RowKey,
            Title = TableValue.String(e, "Title", "Untitled Trip"),
            Description = TableValue.String(e, "Description"),
            TripCompletedOn = completed,
            LengthHours = TableValue.Number(e, "LengthHours", 0),
            LengthHoursIsInteger = e.TryGetValue("LengthHours", out var rawHours) && rawHours is int or long,
            Location = TableValue.String(e, "Location"),
            ElevationGain = TableValue.Number(e, "ElevationGain", 0),
            Difficulty = TableValue.String(e, "Difficulty"),
            MapUrl = TableValue.String(e, "MapUrl"),
            ImageUrl = TableValue.String(e, "ImageUrl"),
            Participants = TableValue.String(e, "Participants"),
            MetersAscend = TableValue.Number(e, "MetersAscend", 0),
            MetersDescend = TableValue.Number(e, "MetersDescend", 0),
            UiaaGrade = Grade(e, "UiaaGrade"),
            AlpineGrade = Grade(e, "AlpineGrade"),
            TripClass = Grade(e, "TripClass"),
            FerataGrade = Grade(e, "FerataGrade"),
            ParkingJson = TableValue.String(e, "ParkingJson"),
            HighPointJson = TableValue.String(e, "HighPointJson"),
            TrackJson = TableValue.String(e, "TrackJson"),
            ModifiedAt = e.Timestamp,
            RowKeyTime = ParseRowKeyTime(e.RowKey),
            CompletedAt = completed.Length == 0 ? null : TableValue.Date(completedRaw),
        };
    }

    /// <summary>Nová entita; chování jako <c>create_trip</c> (výchozí prázdné řetězce, čísla jen když jsou zadaná).</summary>
    public static TableEntity ToNewEntity(string rowKey, TripFormData d)
    {
        var e = new TableEntity(PartitionKey, rowKey)
        {
            ["Title"] = d.Title ?? "",
            ["Description"] = d.Description ?? "",
            ["Location"] = d.Location ?? "",
            ["Difficulty"] = d.Difficulty ?? "",
            ["MapUrl"] = d.MapUrl ?? "",
            ["ImageUrl"] = d.ImageUrl ?? "",
            ["Participants"] = d.Participants ?? "",
            ["UiaaGrade"] = d.UiaaGrade ?? "",
            ["AlpineGrade"] = d.AlpineGrade ?? "",
            ["TripClass"] = d.TripClass ?? "",
            ["FerataGrade"] = d.FerataGrade ?? "",
            ["ParkingJson"] = d.ParkingJson ?? "",
            ["HighPointJson"] = d.HighPointJson ?? "",
            ["TrackJson"] = d.TrackJson ?? "",
        };
        if (d.MetersAscend is { } up) e["MetersAscend"] = up;
        if (d.MetersDescend is { } down) e["MetersDescend"] = down;
        if (d.TripCompletedOn is { } day) e["TripCompletedOn"] = DayText(day);
        if (d.LengthHours is { } hours) e["LengthHours"] = hours;
        if (d.ElevationGain is { } gain) e["ElevationGain"] = gain;
        return e;
    }

    /// <summary>
    /// Entita jen s poslanými vlastnostmi pro merge. Typ existující vlastnosti se zachová; nic, co formulář
    /// neposílá, v ní není, takže to merge nechá být.
    /// </summary>
    public static TableEntity ToMergeEntity(TableEntity existing, TripFormData d)
    {
        var e = new TableEntity(existing.PartitionKey, existing.RowKey);

        // Texty: null = neposílat, "" = vymazat (jako Django).
        Text(e, "Title", d.Title);
        Text(e, "Description", d.Description);
        Text(e, "Participants", d.Participants);
        Text(e, "UiaaGrade", d.UiaaGrade);
        Text(e, "AlpineGrade", d.AlpineGrade);
        Text(e, "TripClass", d.TripClass);
        Text(e, "FerataGrade", d.FerataGrade);
        Text(e, "Location", d.Location);
        Text(e, "Difficulty", d.Difficulty);
        Text(e, "MapUrl", d.MapUrl);
        Text(e, "ImageUrl", d.ImageUrl);

        // JSON pole se prázdná nikdy nezapisují.
        NonEmpty(e, "ParkingJson", d.ParkingJson);
        NonEmpty(e, "HighPointJson", d.HighPointJson);
        NonEmpty(e, "TrackJson", d.TrackJson);
        if (d.TripCompletedOn is { } day)
            e["TripCompletedOn"] = Coerce(existing, "TripCompletedOn", day);

        Number(e, existing, "LengthHours", d.LengthHours);
        Number(e, existing, "ElevationGain", d.ElevationGain);
        Number(e, existing, "MetersAscend", d.MetersAscend);
        Number(e, existing, "MetersDescend", d.MetersDescend);
        return e;
    }

    /// <summary>Entita pro <c>MergeFieldsAsync</c>: hodnoty <c>null</c> se přeskočí, číslo a datum drží typ existující vlastnosti.</summary>
    public static TableEntity ToMergeEntity(TableEntity existing, IReadOnlyDictionary<string, object?> fields)
    {
        var e = new TableEntity(existing.PartitionKey, existing.RowKey);
        foreach (var (key, value) in fields)
        {
            if (value is null || key is "PartitionKey" or "RowKey" or "Timestamp" or "odata.etag")
                continue;
            e[key] = Coerce(existing, key, value);
        }
        return e;
    }

    public static string NewRowKey(DateTimeOffset now) =>
        RowKeyPrefix + now.UtcDateTime.ToString(RowKeyTimeFormat, CultureInfo.InvariantCulture);

    public static DateTimeOffset? ParseRowKeyTime(string? rowKey)
    {
        if (rowKey is null || !rowKey.StartsWith(RowKeyPrefix, StringComparison.Ordinal))
            return null;
        var stamp = rowKey[RowKeyPrefix.Length..];
        if (stamp.Length > 14)
            stamp = stamp[..14];
        return DateTimeOffset.TryParseExact(stamp, RowKeyTimeFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : null;
    }

    private static string Grade(TableEntity e, string key)
    {
        var v = TableValue.String(e, key);
        return v.Length == 0 ? "none" : v;
    }

    private static string CompletedOnText(object? raw) => raw switch
    {
        null => "",
        string s => s,
        DateTimeOffset d => DayText(DateOnly.FromDateTime(d.UtcDateTime)),
        DateTime dt => DayText(DateOnly.FromDateTime(dt)),
        _ => Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "",
    };

    private static string DayText(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static void Text(TableEntity e, string key, string? value)
    {
        if (value is not null)
            e[key] = value;
    }

    private static void NonEmpty(TableEntity e, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            e[key] = value;
    }

    private static void Number(TableEntity e, TableEntity existing, string key, double? value)
    {
        if (value is { } v)
            e[key] = Coerce(existing, key, v);
    }

    private static void Number(TableEntity e, TableEntity existing, string key, int? value)
    {
        if (value is { } v)
            e[key] = Coerce(existing, key, v);
    }

    /// <summary>
    /// Převede novou hodnotu na typ, který má vlastnost v existující entitě, pokud to jde bez ztráty
    /// (Int32/Int64/Double/číselný string/datetime). Jinak (např. 2.5 do Int32) se zapíše přirozený typ hodnoty,
    /// ať se nic neořízne. Bez existující vlastnosti platí přirozený typ.
    /// </summary>
    private static object Coerce(TableEntity existing, string key, object value)
    {
        existing.TryGetValue(key, out var old);

        if (value is DateOnly day)
        {
            return old switch
            {
                DateTimeOffset => new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                DateTime => DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
                _ => DayText(day),
            };
        }

        if (value is not (int or long or double))
            return value;

        var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        var whole = number == Math.Floor(number) && Math.Abs(number) < 9e15;
        return old switch
        {
            int when whole && number is >= int.MinValue and <= int.MaxValue => (int)number,
            long when whole => (long)number,
            double => number,
            string => number.ToString("R", CultureInfo.InvariantCulture),
            _ => value,
        };
    }
}
