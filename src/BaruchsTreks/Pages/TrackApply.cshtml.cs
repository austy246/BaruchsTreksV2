using BaruchsTreks.Http;
using BaruchsTreks.Tracks;
using BaruchsTreks.Trips;
using BaruchsTreks.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BaruchsTreks.Pages;

/// <summary>Potvrzení, které hodnoty z GPX přepíšou už vyplněná pole výletu (<c>views.trip_track_apply</c>).</summary>
[Authorize(Policy = AuthSetup.AdminPolicy)]
public class TrackApplyModel(
    ITripRepository trips,
    IHostEnvironment environment,
    ILogger<TrackApplyModel> logger) : PageModel
{
    public Trip? Trip { get; private set; }

    public TrackStats? Stats { get; private set; }

    /// <summary>Jen odlišné hodnoty; prázdná pole se vyplňují při nahrání.</summary>
    public IReadOnlyList<FieldDiff> Conflicts { get; private set; } = [];

    private IReadOnlyDictionary<string, object> _derived = new Dictionary<string, object>();

    public async Task<IActionResult> OnGetAsync(string id)
    {
        if (!await LoadAsync(id))
            return NotFoundText();
        return Conflicts.Count == 0 ? Redirect(EditUrl(id)) : Page();
    }

    public async Task<IActionResult> OnPostAsync(string id)
    {
        if (!await LoadAsync(id))
            return NotFoundText();
        if (Conflicts.Count == 0)
            return Redirect(EditUrl(id));

        var chosen = Request.Form["fields"].ToHashSet();
        var selected = Conflicts.Where(c => chosen.Contains(c.Field)).ToList();
        if (selected.Count == 0)
        {
            TempData.AddMessage("info", "Hodnoty zůstaly beze změny.");
            return Redirect(EditUrl(id));
        }

        var data = new TripFormData();
        foreach (var conflict in selected)
            data = data.WithDerived(conflict.Field, _derived[conflict.Field]);

        try
        {
            await trips.UpdateAsync(id, data);
            TempData.AddMessage("success", "Přepsáno z GPX: " + string.Join(", ", selected.Select(c => c.Label)) + ".");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error applying track values to {TripId}", id);
            TempData.AddMessage("error", UserErrors.Message(environment, "Uložení selhalo", ex));
        }

        return Redirect(EditUrl(id));
    }

    private async Task<bool> LoadAsync(string id)
    {
        Trip = await trips.GetAsync(id);
        if (Trip is null)
            return false;

        Stats = StoredTrackStats.Read(Trip.TrackJson);
        _derived = TrackFieldComparer.Derived(Stats);
        Conflicts = TrackFieldComparer.Compare(_derived, Trip).Where(d => d.Current is not null).ToList();
        return true;
    }

    private static string EditUrl(string id) => $"/trips/trip/{Uri.EscapeDataString(id)}/edit/";

    private static IActionResult NotFoundText() => new ContentResult
    {
        Content = "Trip not found",
        ContentType = "text/html; charset=utf-8",
        StatusCode = StatusCodes.Status404NotFound,
    };
}
