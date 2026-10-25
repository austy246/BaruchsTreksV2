using System.Globalization;
using BaruchsTreks.Strava;
using BaruchsTreks.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BaruchsTreks.Pages;

/// <summary>Seznam vlastních aktivit připojeného sportovce k importu (<c>views.strava_activities</c>).</summary>
[Authorize(Policy = AuthSetup.AdminPolicy)]
public class StravaActivitiesModel(StravaService strava, ILogger<StravaActivitiesModel> logger) : PageModel
{
    public bool Configured { get; private set; }

    public bool Connected { get; private set; }

    public IReadOnlyList<StravaActivity> Activities { get; private set; } = [];

    /// <summary>Výlet, ke kterému se trasa připojí; prázdný = import do nového výletu.</summary>
    public string TripId { get; private set; } = "";

    public int PageNumber { get; private set; } = 1;

    public async Task OnGetAsync([FromQuery(Name = "trip_id")] string? tripId, [FromQuery(Name = "page")] string? page)
    {
        TripId = tripId ?? "";
        PageNumber = int.TryParse(page, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? Math.Max(1, number) : 1;
        Configured = strava.IsConfigured;

        var userName = User.Identity!.Name!;
        if (!Configured || !await strava.IsConnectedAsync(userName))
            return;

        Connected = true;
        try
        {
            Activities = await strava.ListActivitiesAsync(userName, PageNumber);
        }
        catch (StravaNotConnected ex)
        {
            Connected = false;
            TempData.AddMessage("warning", ex.Message);
        }
        catch (Exception ex) when (ex is StravaError or StravaNotConfigured)
        {
            logger.LogWarning("Listing Strava activities failed: {Message}", ex.Message);
            TempData.AddMessage("error", ex.Message);
        }
    }

    public static string EditUrl(string tripId) => $"/trips/trip/{Uri.EscapeDataString(tripId)}/edit/";

    /// <summary>Django <c>date:"j. n. Y"</c>: den a měsíc bez nul.</summary>
    public static string Date(DateOnly? date) =>
        date is { } d ? string.Create(CultureInfo.InvariantCulture, $"{d.Day}. {d.Month}. {d.Year}") : "";
}
