using System.Globalization;
using BaruchsTreks.Tracks;
using BaruchsTreks.Trips;
using BaruchsTreks.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BaruchsTreks.Pages;

/// <summary>
/// Kopie výletu (<c>views.trip_copy</c>): stejná pole, název s " (copy)", bez fotek; potom úprava kopie.
/// Na rozdíl od Djanga se kopíruje i soubor trasy, aby uložené statistiky nezůstaly bez GPX.
/// </summary>
[Authorize(Policy = AuthSetup.AdminPolicy)]
public class TripCopyModel(ITripRepository trips, ITrackStore tracks) : PageModel
{
    public Task<IActionResult> OnGetAsync(string id) => CopyAsync(id);

    public Task<IActionResult> OnPostAsync(string id) => CopyAsync(id);

    private async Task<IActionResult> CopyAsync(string id)
    {
        var trip = await trips.GetAsync(id);
        if (trip is null)
        {
            return new ContentResult
            {
                Content = "Original trip not found.",
                ContentType = "text/html; charset=utf-8",
                StatusCode = StatusCodes.Status404NotFound,
            };
        }

        var created = await trips.CreateAsync(new TripFormData
        {
            Title = $"{trip.Title} (copy)",
            Description = trip.Description,
            TripCompletedOn = CompletedOn(trip.TripCompletedOn),
            LengthHours = trip.LengthHours,
            Participants = trip.Participants,
            MetersAscend = (int)trip.MetersAscend,
            MetersDescend = (int)trip.MetersDescend,
            UiaaGrade = trip.UiaaGrade,
            AlpineGrade = trip.AlpineGrade,
            TripClass = trip.TripClass,
            FerataGrade = trip.FerataGrade,
            ParkingJson = trip.ParkingJson,
            HighPointJson = trip.HighPointJson,
            TrackJson = trip.TrackJson,
            Location = trip.Location,
            Difficulty = trip.Difficulty,
            MapUrl = trip.MapUrl,
            ImageUrl = trip.ImageUrl,
            ElevationGain = (int)trip.ElevationGain,
        });

        var gpx = await tracks.GetGpxAsync(id);
        var geojson = await tracks.GetGeoJsonAsync(id);
        if (gpx is not null && geojson is not null)
            await tracks.SaveAsync(created, gpx, geojson);

        return Redirect($"/trips/trip/{Uri.EscapeDataString(created)}/edit/");
    }

    private static DateOnly? CompletedOn(string text)
    {
        var day = text.Length > 10 ? text[..10] : text;
        return DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }
}
