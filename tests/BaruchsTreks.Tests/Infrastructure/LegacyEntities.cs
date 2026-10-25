using Azure.Data.Tables;

namespace BaruchsTreks.Tests;

/// <summary>
/// Tvary entit ve stylu produkčních dat (zapsal je starý Python kód). Slouží k ověření,
/// že nová aplikace čte i mění data bez migrace.
/// </summary>
public static class LegacyEntities
{
    /// <summary>
    /// Starý tvar A: čísla jako Int32, datum jako řetězec, grade prázdné, JSON jako řetězce
    /// s překlepem Longtitude, bez Participants a MetersDescend. Má ImageUrl, MapUrl a vlastnost navíc.
    /// </summary>
    public static TableEntity A(string rowKey) => new("Trips", rowKey)
    {
        ["Title"] = "Legacy A",
        ["Description"] = "Starý záznam",
        ["TripCompletedOn"] = "2025-08-15",
        ["LengthHours"] = 6,
        ["Location"] = "Tatry",
        ["ElevationGain"] = 1200,
        ["Difficulty"] = "",
        ["MapUrl"] = "https://mapy.example/a",
        ["ImageUrl"] = "https://img.example/a.jpg",
        ["MetersAscend"] = 1200,
        ["UiaaGrade"] = "",
        ["AlpineGrade"] = "PD",
        ["TripClass"] = "",
        ["FerataGrade"] = "",
        ["ParkingJson"] = """{"Latitude": 49.1, "Longtitude": 20.2}""",
        ["HighPointJson"] = """{"Latitude": 49.2, "Longtitude": 20.1}""",
        ["Extra"] = "zůstane",
    };

    /// <summary>
    /// Starý tvar B: Int64 a Double čísla, TripCompletedOn jako datetime vlastnost, TrackJson,
    /// chybějící Location a Title.
    /// </summary>
    public static TableEntity B(string rowKey) => new("Trips", rowKey)
    {
        ["Description"] = "",
        ["TripCompletedOn"] = new DateTimeOffset(2024, 7, 1, 0, 0, 0, TimeSpan.Zero),
        ["LengthHours"] = 4.5,
        ["ElevationGain"] = 800L,
        ["MetersAscend"] = 810.5,
        ["MetersDescend"] = 805L,
        ["Participants"] = "Já a Baruch",
        ["UiaaGrade"] = "IV",
        ["TripClass"] = "Trail",
        ["TrackJson"] = """{"distance_km": 12.3}""",
    };
}
