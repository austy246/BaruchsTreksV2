using System.Text;
using BaruchsTreks.Http;
using BaruchsTreks.Strava;
using BaruchsTreks.Tracks;
using BaruchsTreks.Trips;
using BaruchsTreks.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BaruchsTreks.Pages;

/// <summary>
/// Import trasy aktivity ze Stravy do nového nebo existujícího výletu (<c>views.strava_import</c>, jen POST). Dál se
/// postupuje jako po nahrání GPX: prázdná pole se vyplní, odlišné hodnoty čekají na potvrzení na stránce TrackApply.
/// </summary>
[Authorize(Policy = AuthSetup.AdminPolicy)]
public class StravaImportModel(
    StravaService strava,
    ITripRepository trips,
    ITrackStore tracks,
    IHostEnvironment environment,
    ILogger<StravaImportModel> logger) : PageModel
{
    private const string ActivitiesUrl = "/trips/strava/";

    public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);

    public async Task<IActionResult> OnPostAsync(long activityId)
    {
        var tripId = Request.Form["trip_id"].ToString();

        StravaGpx download;
        ParsedTrack parsed;
        try
        {
            download = await strava.ImportGpxAsync(User.Identity!.Name!, activityId);
            parsed = GpxParser.Parse(download.Gpx);
        }
        catch (Exception ex) when (ex is StravaNotConfigured or StravaNotConnected or StravaError)
        {
            logger.LogWarning("Strava import of activity {ActivityId} failed: {Message}", activityId, ex.Message);
            return Fail(ex.Message);
        }
        catch (GpxParseError ex)
        {
            logger.LogWarning(ex, "Strava import of activity {ActivityId} produced an unreadable GPX", activityId);
            return Fail(UserErrors.GpxMessage(environment, ex));
        }

        var activity = download.Activity;
        TrackImportPlan plan;
        if (tripId.Length > 0)
        {
            var trip = await trips.GetAsync(tripId);
            if (trip is null)
                return Fail("Trip nebyl nalezen.");

            // Stejné pravidlo jako u nahrání souboru: prázdná pole se vyplní, o zbytek se zeptáme.
            plan = TrackImportPlan.Create(parsed, new TripFormData(), trip);
            try
            {
                await trips.UpdateAsync(tripId, plan.Data);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error saving Strava track to {TripId}", tripId);
                return Fail(UserErrors.Message(environment, "Uložení trasy selhalo", ex));
            }
        }
        else
        {
            // Nový výlet nemá co přepisovat, takže se vyplní všechno z trasy a datum určí Strava.
            plan = TrackImportPlan.Create(parsed, new TripFormData(), new Trip());
            var data = plan.Data with
            {
                Title = activity.Name.Length > 0 ? activity.Name : $"Strava {activityId}",
                TripClass = StravaTripClass.For(activity.SportType),
                TripCompletedOn = activity.StartDate ?? plan.Data.TripCompletedOn,
            };
            try
            {
                tripId = await trips.CreateAsync(data);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error creating trip from Strava activity {ActivityId}", activityId);
                return Fail(UserErrors.Message(environment, "Vytvoření tripu selhalo", ex));
            }
        }

        try
        {
            await tracks.SaveAsync(tripId, download.Gpx, Encoding.UTF8.GetBytes(parsed.GeoJson));
            TempData.AddMessage("success",
                $"Trasa ze Stravy importována: {DjangoFormat.Float(parsed.Stats.DistanceKm)} km, ↑{parsed.Stats.MetersAscend} m.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error saving Strava track for {TripId}", tripId);
            TempData.AddMessage("error", UserErrors.Message(environment, "Trasu se nepodařilo uložit", ex));
        }

        return Redirect(plan.Conflicts.Count > 0
            ? TrackImportPlan.ApplyUrl(tripId)
            : $"/trips/trip/{Uri.EscapeDataString(tripId)}/edit/");
    }

    private IActionResult Fail(string message)
    {
        TempData.AddMessage("error", message);
        return Redirect(ActivitiesUrl);
    }
}
