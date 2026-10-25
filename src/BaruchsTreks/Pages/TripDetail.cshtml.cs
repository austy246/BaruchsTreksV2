using BaruchsTreks.Photos;
using BaruchsTreks.Tracks;
using BaruchsTreks.Trips;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BaruchsTreks.Pages;

/// <summary>Detail výletu: popis, fotky, statistiky trasy a mapa (<c>views.trip_detail</c>).</summary>
public class TripDetailModel(
    ITripRepository trips,
    IPhotoStore photos,
    IConfiguration configuration,
    ILogger<TripDetailModel> logger) : PageModel
{
    /// <summary><c>null</c> = výlet neexistuje (stránka ukáže „Trip Not Found“ se stavem 404).</summary>
    public Trip? Trip { get; private set; }

    public IReadOnlyList<string> Photos { get; private set; } = [];

    /// <summary>Statistiky trasy z <c>TrackJson</c>, nebo <c>null</c> (bez trasy i při nečitelném JSON).</summary>
    public TrackStats? Stats { get; private set; }

    public string MapyApiKey { get; private set; } = "";

    /// <summary>Mapa se ukáže, když má výlet bod startu, vrcholu nebo trasu.</summary>
    public bool ShowMap =>
        Trip is not null && (Trip.HighPointJson.Length > 0 || Trip.ParkingJson.Length > 0 || Stats is not null);

    public async Task<IActionResult> OnGetAsync(string id)
    {
        // Neexistující i podivné id (apostrof, lomítko, ...) je prostě neexistující výlet.
        Trip = await trips.GetAsync(id);
        if (Trip is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return Page();
        }

        Photos = await photos.ListAsync(Trip.Id);
        Stats = StoredTrackStats.Read(Trip.TrackJson);
        if (Stats is null && !string.IsNullOrWhiteSpace(Trip.TrackJson))
            logger.LogWarning("Could not parse TrackJson for trip {TripId}", Trip.Id);

        MapyApiKey = configuration["Mapy:ApiKey"] ?? "";
        return Page();
    }
}
