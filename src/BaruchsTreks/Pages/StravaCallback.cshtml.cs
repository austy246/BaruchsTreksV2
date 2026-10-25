using BaruchsTreks.Http;
using BaruchsTreks.Strava;
using BaruchsTreks.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BaruchsTreks.Pages;

/// <summary>Přesměrování ze Stravy po udělení přístupu: ověří <c>state</c> a uloží tokeny (<c>views.strava_callback</c>).</summary>
[Authorize(Policy = AuthSetup.AdminPolicy)]
public class StravaCallbackModel(
    StravaService strava,
    StravaOAuthState oauthState,
    IHostEnvironment environment,
    ILogger<StravaCallbackModel> logger) : PageModel
{
    private const string ActivitiesUrl = "/trips/strava/";

    public async Task<IActionResult> OnGetAsync(string? error, string? state, string? code)
    {
        var userName = User.Identity!.Name!;

        // Stav se spotřebuje vždy, i když požadavek skončí chybou.
        var tripId = oauthState.Consume(HttpContext, userName, state);

        if (!string.IsNullOrEmpty(error))
        {
            // Hodnota je z URL: zkrátit, aby hláška nepřetekla cookie s TempData.
            var shown = error.Length > 100 ? error[..100] : error;
            return Fail($"Strava odmítla přístup: {shown}");
        }

        if (tripId is null)
            return Fail("Neplatný stav OAuth požadavku, zkus propojení znovu.");

        if (string.IsNullOrEmpty(code))
            return Fail("Strava nevrátila autorizační kód.");

        try
        {
            await strava.ConnectAsync(userName, code);
            TempData.AddMessage("success", "Účet Strava byl propojen.");
        }
        catch (Exception ex) when (ex is StravaNotConfigured or StravaError)
        {
            logger.LogWarning("Connecting Strava failed: {Message}", ex.Message);
            TempData.AddMessage("error", ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error saving Strava tokens");
            TempData.AddMessage("error", UserErrors.Message(environment, "Uložení tokenů selhalo", ex));
        }

        return Redirect(tripId.Length > 0 ? $"{ActivitiesUrl}?trip_id={Uri.EscapeDataString(tripId)}" : ActivitiesUrl);
    }

    private IActionResult Fail(string message)
    {
        TempData.AddMessage("error", message);
        return Redirect(ActivitiesUrl);
    }
}
