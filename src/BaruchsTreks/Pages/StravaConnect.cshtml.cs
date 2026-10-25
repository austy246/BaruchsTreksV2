using BaruchsTreks.Strava;
using BaruchsTreks.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BaruchsTreks.Pages;

/// <summary>Pošle uživatele na Stravu udělit přístup k aktivitám (<c>views.strava_connect</c>).</summary>
[Authorize(Policy = AuthSetup.AdminPolicy)]
public class StravaConnectModel(StravaService strava, StravaOAuthState oauthState) : PageModel
{
    public IActionResult OnGet([FromQuery(Name = "trip_id")] string? tripId)
    {
        if (!strava.IsConfigured)
        {
            TempData.AddMessage("error", "Strava není nakonfigurovaná - nastav STRAVA_CLIENT_ID a STRAVA_CLIENT_SECRET.");
            return Redirect("/trips/strava/");
        }

        var state = oauthState.Issue(HttpContext, User.Identity!.Name!, tripId ?? "");
        // Adresa je zaregistrovaná u Stravy jako callback domain; cesta zůstává /trips/strava/callback/.
        var redirectUri = $"{Request.Scheme}://{Request.Host}/trips/strava/callback/";
        return Redirect(strava.AuthorizeUrl(redirectUri, state));
    }
}
