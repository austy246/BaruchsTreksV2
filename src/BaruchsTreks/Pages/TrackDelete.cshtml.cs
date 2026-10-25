using BaruchsTreks.Http;
using BaruchsTreks.Tracks;
using BaruchsTreks.Trips;
using BaruchsTreks.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BaruchsTreks.Pages;

/// <summary>Smazání trasy výletu a jejích statistik (<c>views.trip_track_delete</c>, jen POST).</summary>
[Authorize(Policy = AuthSetup.AdminPolicy)]
public class TrackDeleteModel(
    ITripRepository trips,
    ITrackStore tracks,
    IHostEnvironment environment,
    ILogger<TrackDeleteModel> logger) : PageModel
{
    public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);

    public async Task<IActionResult> OnPostAsync(string id)
    {
        if (await trips.GetAsync(id) is null)
        {
            return new ContentResult
            {
                Content = "Trip not found",
                ContentType = "text/html; charset=utf-8",
                StatusCode = StatusCodes.Status404NotFound,
            };
        }

        try
        {
            // Nejdřív statistiky: kdyby smazání blobů selhalo, zůstane jen nepoužitý soubor, ne statistiky bez souboru.
            await trips.MergeFieldsAsync(id, new Dictionary<string, object?> { ["TrackJson"] = "" });
            await tracks.DeleteAsync(id);
            TempData.AddMessage("success", "Trasa byla smazána.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error deleting track of {TripId}", id);
            TempData.AddMessage("error", UserErrors.Message(environment, "Trasu se nepodařilo smazat", ex));
        }

        return Redirect($"/trips/trip/{Uri.EscapeDataString(id)}/edit/");
    }
}
