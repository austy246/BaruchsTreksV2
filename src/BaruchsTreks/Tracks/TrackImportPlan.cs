using BaruchsTreks.Trips;

namespace BaruchsTreks.Tracks;

/// <summary>
/// Co se stane s poli výletu po nahrání trasy (soubor GPX i import ze Stravy): prázdná pole se vyplní hned
/// (<see cref="Fills"/>, už zapsaná v <see cref="Data"/>), cokoli by přepsalo zadanou hodnotu čeká na potvrzení
/// (<see cref="Conflicts"/>, stránka <c>TrackApply</c>).
/// </summary>
public sealed record TrackImportPlan(
    TripFormData Data,
    IReadOnlyList<FieldDiff> Fills,
    IReadOnlyList<FieldDiff> Conflicts)
{
    /// <param name="parsed">Rozparsovaná trasa.</param>
    /// <param name="data">Data k zápisu; doplní se <c>TrackJson</c> a vyplněná pole.</param>
    /// <param name="current">Hodnoty, se kterými se GPX porovnává (odeslaný formulář, uložený výlet, nebo prázdný <see cref="Trip"/> u nového).</param>
    public static TrackImportPlan Create(ParsedTrack parsed, TripFormData data, Trip current)
    {
        var derived = TrackFieldComparer.Derived(parsed.Stats);
        var fills = new List<FieldDiff>();
        var conflicts = new List<FieldDiff>();
        foreach (var diff in TrackFieldComparer.Compare(derived, current))
            (diff.Current is null ? fills : conflicts).Add(diff);

        data = data with { TrackJson = parsed.Stats.ToJson() };
        foreach (var fill in fills)
            data = data.WithDerived(fill.Field, derived[fill.Field]);
        return new TrackImportPlan(data, fills, conflicts);
    }

    /// <summary>Adresa potvrzení přepisu pro výlet <paramref name="tripId"/>.</summary>
    public static string ApplyUrl(string tripId) => $"/trips/trip/{Uri.EscapeDataString(tripId)}/track/apply/";
}
