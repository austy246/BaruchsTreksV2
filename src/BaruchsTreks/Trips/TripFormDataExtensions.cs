using System.Globalization;

namespace BaruchsTreks.Trips;

public static class TripFormDataExtensions
{
    /// <summary>
    /// Doplní hodnotu pole z GPX (klíče jako v <c>TrackFieldComparer</c>: <c>meters_ascend</c>, <c>length_hours</c>, ...).
    /// Neznámé pole se ignoruje.
    /// </summary>
    public static TripFormData WithDerived(this TripFormData data, string field, object value) => field switch
    {
        "meters_ascend" => data with { MetersAscend = Convert.ToInt32(value, CultureInfo.InvariantCulture) },
        "meters_descend" => data with { MetersDescend = Convert.ToInt32(value, CultureInfo.InvariantCulture) },
        "length_hours" => data with { LengthHours = Convert.ToDouble(value, CultureInfo.InvariantCulture) },
        "trip_completed_on" => DateOnly.TryParseExact(Convert.ToString(value, CultureInfo.InvariantCulture), "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? data with { TripCompletedOn = day }
            : data,
        "parking_json" => data with { ParkingJson = (string)value },
        "high_point_json" => data with { HighPointJson = (string)value },
        _ => data,
    };
}
