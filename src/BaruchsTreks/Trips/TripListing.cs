namespace BaruchsTreks.Trips;

/// <summary>Filtr a řazení stránky „všechny výlety“ (chování <c>all_trips</c> z Django).</summary>
public static class TripListing
{
    /// <summary><c>completed</c> = s datem, <c>future</c> = bez data, cokoli jiného = všechny.</summary>
    public static IReadOnlyList<Trip> Filter(IEnumerable<Trip> trips, string filter) => filter switch
    {
        "completed" => trips.Where(IsCompleted).ToList(),
        "future" => trips.Where(t => !IsCompleted(t)).ToList(),
        _ => trips.ToList(),
    };

    public static bool IsCompleted(Trip trip) => !string.IsNullOrEmpty(trip.TripCompletedOn);

    /// <summary>
    /// <c>created</c>, <c>completed</c>, <c>name</c>, jinak <c>modified</c>; <c>asc</c> vzestupně, jinak sestupně.
    /// Stabilní jako Python <c>sorted</c> (shodné klíče zůstávají v původním pořadí).
    /// </summary>
    public static IReadOnlyList<Trip> Sort(IEnumerable<Trip> trips, string key, string dir)
    {
        var ascending = dir == "asc";
        return key switch
        {
            "created" => Order(trips, t => t.RowKeyTime ?? DateTimeOffset.MinValue, ascending),
            "completed" => Order(trips, t => t.CompletedAt ?? DateTimeOffset.MinValue, ascending),
            "name" => ascending
                ? trips.OrderBy(Name, StringComparer.Ordinal).ToList()
                : trips.OrderByDescending(Name, StringComparer.Ordinal).ToList(),
            _ => Order(trips, ModifiedKey, ascending),
        };
    }

    /// <summary>Výchozí pořadí seznamu: <c>ModifiedAt</c>, jinak čas z RowKey, jinak nejstarší.</summary>
    public static DateTimeOffset ModifiedKey(Trip t) => t.ModifiedAt ?? t.RowKeyTime ?? DateTimeOffset.MinValue;

    private static string Name(Trip t) => (t.Title ?? "").ToLowerInvariant();

    private static List<Trip> Order(IEnumerable<Trip> trips, Func<Trip, DateTimeOffset> key, bool ascending) =>
        (ascending ? trips.OrderBy(key) : trips.OrderByDescending(key)).ToList();
}
