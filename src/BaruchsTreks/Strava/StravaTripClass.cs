namespace BaruchsTreks.Strava;

/// <summary>Typy sportu ze Stravy převedené na vlastní třídy výletů (<c>STRAVA_TRIP_CLASS</c>).</summary>
public static class StravaTripClass
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        ["Hike"] = "Trail",
        ["Walk"] = "Trail",
        ["Snowshoe"] = "Trail",
        ["Run"] = "Run",
        ["TrailRun"] = "Run",
        ["BackcountrySki"] = "Skialp",
        ["NordicSki"] = "Skialp",
        ["AlpineSki"] = "Slope",
        ["Snowboard"] = "Slope",
        ["RockClimbing"] = "Climb",
    };

    /// <summary>Třída výletu, nebo prázdný řetězec, když sport nemá protějšek.</summary>
    public static string For(string? sportType) => sportType is not null && Map.TryGetValue(sportType, out var tripClass) ? tripClass : "";
}
