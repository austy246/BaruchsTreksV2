using System.Text.Json;

namespace BaruchsTreks.Tracks;

public static class StoredTrackStats
{
    /// <summary>
    /// Statistiky uložené u výletu (<c>TrackJson</c>), nebo <c>null</c>: prázdný text, nevalidní JSON i prázdný objekt
    /// (v Django je prázdný slovník nepravdivá hodnota) znamenají, že výlet nemá trasu.
    /// </summary>
    public static TrackStats? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.EnumerateObject().Any())
                return null;
            return TrackStats.FromJson(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
