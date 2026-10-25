namespace BaruchsTreks.Strava;

/// <summary>Sekce <c>Strava</c> v konfiguraci (<c>Strava__ClientId</c>, <c>Strava__ClientSecret</c>). Bez obou je import vypnutý.</summary>
public sealed class StravaOptions
{
    public const string SectionName = "Strava";

    public string ClientId { get; set; } = "";

    public string ClientSecret { get; set; } = "";
}
