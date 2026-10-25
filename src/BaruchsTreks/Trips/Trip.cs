namespace BaruchsTreks.Trips;

/// <summary>
/// Výlet načtený z tabulky. Všechna pole jsou vyplněná (výchozí hodnoty jako v Django); čísla jsou
/// <see cref="double"/>, protože produkční data míchají Int32/Int64/Double.
/// </summary>
public sealed record Trip
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "Untitled Trip";
    public string Description { get; init; } = "";

    /// <summary>Řetězec z entity (nejčastěji YYYY-MM-DD) nebo datum z datetime vlastnosti jako YYYY-MM-DD. Prázdný = nesplněno.</summary>
    public string TripCompletedOn { get; init; } = "";

    public double LengthHours { get; init; }

    /// <summary>
    /// <c>LengthHours</c> je v tabulce uložené jako Int32/Int64 (Python <c>int</c>). Django vypisuje hodnotu podle
    /// typu, který přečte (<c>16</c> vs. <c>3.0</c>), takže zobrazení musí typ zachovat.
    /// </summary>
    public bool LengthHoursIsInteger { get; init; }

    /// <summary>Text délky jako Django <c>{{ trip.length_hours }}</c>: celé číslo bez ".0", Double jako Python float.</summary>
    public string LengthHoursText => LengthHoursIsInteger ? DjangoFormat.Number(LengthHours) : DjangoFormat.Float(LengthHours);
    public string Location { get; init; } = "";
    public double ElevationGain { get; init; }
    public string Difficulty { get; init; } = "";
    public string MapUrl { get; init; } = "";
    public string ImageUrl { get; init; } = "";
    public string Participants { get; init; } = "";
    public double MetersAscend { get; init; }
    public double MetersDescend { get; init; }

    /// <summary>Prázdné hodnoty se čtou jako <c>none</c>.</summary>
    public string UiaaGrade { get; init; } = "none";
    public string AlpineGrade { get; init; } = "none";
    public string TripClass { get; init; } = "none";
    public string FerataGrade { get; init; } = "none";

    /// <summary>JSON s klíči <c>Latitude</c> a <c>Longtitude</c> (překlep zůstává).</summary>
    public string ParkingJson { get; init; } = "";
    public string HighPointJson { get; init; } = "";
    public string TrackJson { get; init; } = "";

    /// <summary>Table <c>Timestamp</c>.</summary>
    public DateTimeOffset? ModifiedAt { get; init; }

    /// <summary>Čas odvozený z RowKey <c>trip_yyyyMMddHHmmss</c> (UTC).</summary>
    public DateTimeOffset? RowKeyTime { get; init; }

    /// <summary>Rozparsované <see cref="TripCompletedOn"/>; bez zóny = UTC.</summary>
    public DateTimeOffset? CompletedAt { get; init; }
}
