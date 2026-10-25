using BaruchsTreks.Http;
using BaruchsTreks.Tracks;
using BaruchsTreks.Trips;
using BaruchsTreks.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BaruchsTreks.Pages;

/// <summary>Potvrzení a smazání výletu (<c>views.trip_delete</c>): smaže entitu a blob trasy, fotky zůstávají jako dřív.</summary>
[Authorize(Policy = AuthSetup.AdminPolicy)]
public class TripDeleteModel(
    ITripRepository trips,
    ITrackStore tracks,
    IHostEnvironment environment,
    ILogger<TripDeleteModel> logger) : PageModel
{
    public string TripId { get; private set; } = "";

    public void OnGet(string id) => TripId = id;

    public async Task<IActionResult> OnPostAsync(string id)
    {
        try
        {
            if (await trips.GetAsync(id) is null)
            {
                TempData.AddMessage("error", "Failed to delete trip: trip not found.");
                return Redirect("/trips/all/");
            }

            await trips.DeleteAsync(id);
            await tracks.DeleteAsync(id);
            TempData.AddMessage("success", "Trip deleted successfully.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error deleting trip {TripId}", id);
            TempData.AddMessage("error", UserErrors.Message(environment, "Failed to delete trip", ex));
        }

        return Redirect("/trips/all/");
    }
}
