using BaruchsTreks.Trips;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BaruchsTreks.Pages;

/// <summary>Seznam všech výletů s filtrem a řazením (<c>views.all_trips</c>).</summary>
public class AllTripsModel(ITripRepository trips) : PageModel
{
    public IReadOnlyList<Trip> Trips { get; private set; } = [];
    public string CurrentFilter { get; private set; } = "all";
    public string CurrentSort { get; private set; } = "modified";
    public string CurrentDir { get; private set; } = "desc";
    public int TotalCount { get; private set; }
    public int CompletedCount { get; private set; }
    public int FutureCount { get; private set; }

    public async Task OnGetAsync()
    {
        var all = await trips.ListAsync();

        // Jako request.GET.get(): chybějící parametr má výchozí hodnotu, u opakovaného platí poslední.
        CurrentFilter = Query("filter", "all");
        CurrentSort = Query("sort", "modified");
        CurrentDir = Query("dir", "desc");

        Trips = TripListing.Sort(TripListing.Filter(all, CurrentFilter), CurrentSort, CurrentDir);
        TotalCount = all.Count;
        CompletedCount = all.Count(TripListing.IsCompleted);
        FutureCount = TotalCount - CompletedCount;
    }

    private string Query(string key, string fallback) =>
        Request.Query.TryGetValue(key, out var values) && values.Count > 0 ? values[^1] ?? "" : fallback;
}
