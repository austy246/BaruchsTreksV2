using System.Text.Json;
using System.Text.Json.Serialization;

namespace BaruchsTreks.Tracks;

/// <summary>One GPX point. Elevation and time are optional in real files.</summary>
public sealed record TrackPoint(double Lat, double Lng, double? Ele, DateTimeOffset? Time);

/// <summary>Start of the track. The misspelling of <c>Longtitude</c> is part of the stored data format.</summary>
public sealed record StartPoint(
    [property: JsonPropertyName("Latitude")] double Latitude,
    [property: JsonPropertyName("Longtitude")] double Longtitude);

public sealed record HighPoint(
    [property: JsonPropertyName("Latitude")] double Latitude,
    [property: JsonPropertyName("Longtitude")] double Longtitude,
    [property: JsonPropertyName("Elevation")] double Elevation);

/// <summary>
/// The statistics stored as <c>TrackJson</c> on the trip entity. Field names and rounding follow
/// <c>trips/gpx_service.py</c> (<c>stats</c> plus <c>track_record</c>), so data written by the
/// Django app and by this one is interchangeable.
/// </summary>
public sealed record TrackStats
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    [JsonPropertyName("distance_km")] public double DistanceKm { get; init; }
    [JsonPropertyName("meters_ascend")] public int MetersAscend { get; init; }
    [JsonPropertyName("meters_descend")] public int MetersDescend { get; init; }
    [JsonPropertyName("duration_hours")] public double? DurationHours { get; init; }
    [JsonPropertyName("point_count")] public int PointCount { get; init; }
    [JsonPropertyName("original_point_count")] public int OriginalPointCount { get; init; }
    [JsonPropertyName("min_elevation")] public int? MinElevation { get; init; }
    [JsonPropertyName("max_elevation")] public int? MaxElevation { get; init; }

    /// <summary>[[minLat, minLng], [maxLat, maxLng]].</summary>
    [JsonPropertyName("bounds")] public double[][] Bounds { get; init; } = [];

    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("start")] public StartPoint? Start { get; init; }

    [JsonPropertyName("high_point")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HighPoint? HighPoint { get; init; }

    /// <summary>Date (YYYY-MM-DD) of the first timestamp, in the offset the file wrote it with.</summary>
    [JsonPropertyName("started_on")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StartedOn { get; init; }

    /// <summary>Serializes to the <c>TrackJson</c> string. Always uses dots as decimal separator.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static TrackStats? FromJson(string json) => JsonSerializer.Deserialize<TrackStats>(json, Options);
}
