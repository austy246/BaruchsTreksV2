using BaruchsTreks.Strava;
using BaruchsTreks.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BaruchsTreks.Pages;

/// <summary>Zapomene uložené tokeny (<c>views.strava_disconnect</c>, jen POST).</summary>
[Authorize(Policy = AuthSetup.AdminPolicy)]
public class StravaDisconnectModel(StravaService strava) : PageModel
{
    public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);

    public async Task<IActionResult> OnPostAsync()
    {
        await strava.DisconnectAsync(User.Identity!.Name!);
        TempData.AddMessage("success", "Propojení se Stravou bylo zrušeno.");
        return Redirect("/trips/strava/");
    }
}
