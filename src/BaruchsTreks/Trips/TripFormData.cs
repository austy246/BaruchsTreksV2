namespace BaruchsTreks.Trips;

/// <summary>
/// Data pro zápis výletu. <c>null</c> = vlastnost se neposílá (u úpravy zůstane, jak je). Prázdný řetězec
/// se zapíše (vymaže text), kromě JSON polí, která se prázdná nikdy nezapisují (smazání trasy jde přes
/// <see cref="ITripRepository.MergeFieldsAsync"/>). <see cref="TripCompletedOn"/> se nikdy nemaže.
/// </summary>
public sealed record TripFormData
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public DateOnly? TripCompletedOn { get; init; }
    public double? LengthHours { get; init; }
    public string? Participants { get; init; }
    public int? MetersAscend { get; init; }
    public int? MetersDescend { get; init; }
    public string? UiaaGrade { get; init; }
    public string? AlpineGrade { get; init; }
    public string? TripClass { get; init; }
    public string? FerataGrade { get; init; }
    public string? ParkingJson { get; init; }
    public string? HighPointJson { get; init; }
    public string? TrackJson { get; init; }

    // Pole mimo formulář úprav, která potřebuje kopie výletu.
    public string? Location { get; init; }
    public string? Difficulty { get; init; }
    public string? MapUrl { get; init; }
    public string? ImageUrl { get; init; }
    public int? ElevationGain { get; init; }
}
