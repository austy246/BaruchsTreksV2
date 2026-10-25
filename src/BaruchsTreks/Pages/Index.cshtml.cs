using BaruchsTreks.Photos;
using BaruchsTreks.Trips;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BaruchsTreks.Pages;

/// <summary>Karta výletu na úvodní stránce (výlet a první fotka, nebo <c>null</c>).</summary>
public sealed record TripCard(Trip Trip, string? FirstPhoto);

/// <summary>Úvodní stránka: šest naposledy změněných výletů a první fotka každého (<c>views.index</c>).</summary>
public class IndexModel(
    ITripRepository trips,
    IPhotoStore photos,
    IHostEnvironment environment,
    ILogger<IndexModel> logger) : PageModel
{
    private const int FeaturedCount = 6;

    public IReadOnlyList<TripCard> Cards { get; private set; } = [];

    /// <summary>Chyba při načtení; stránka pak ukáže <c>error.html</c> se stavem 500.</summary>
    public bool Failed { get; private set; }

    /// <summary>Text výjimky pro uživatele. Mimo Development vždy jen obecná hláška.</summary>
    public string ErrorMessage { get; private set; } = "";

    /// <summary>Podrobnosti výjimky (jen Development), jinak <c>null</c> a karta s technickými podrobnostmi se nezobrazí.</summary>
    public string? TechnicalDetails { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        try
        {
            var featured = (await trips.ListAsync()).Take(FeaturedCount).ToList();
            var firstPhotos = await Task.WhenAll(featured.Select(async trip => (await photos.ListAsync(trip.Id)).FirstOrDefault()));
            Cards = featured.Select((trip, i) => new TripCard(trip, firstPhotos[i])).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error in index view");
            Failed = true;
            if (environment.IsDevelopment())
            {
                ErrorMessage = ex.Message;
                TechnicalDetails = ex.ToString();
            }
            else
            {
                ErrorMessage = "Sorry, something went wrong while loading the trips. Please try again later.";
            }

            Response.StatusCode = StatusCodes.Status500InternalServerError;
        }

        return Page();
    }
}
