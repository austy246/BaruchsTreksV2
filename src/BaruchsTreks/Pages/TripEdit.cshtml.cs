using System.Text;
using BaruchsTreks.Http;
using BaruchsTreks.Photos;
using BaruchsTreks.Tracks;
using BaruchsTreks.Trips;
using BaruchsTreks.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BaruchsTreks.Pages;

/// <summary>Založení (<c>/trips/trip/new/</c>) a úprava výletu včetně nahrání GPX a fotek (<c>views.trip_edit</c>).</summary>
[Authorize(Policy = AuthSetup.AdminPolicy)]
public class TripEditModel(
    ITripRepository trips,
    IPhotoStore photos,
    ITrackStore tracks,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<TripEditModel> logger) : PageModel
{
    public bool IsNew { get; private set; }

    /// <summary>Uložený výlet (jen u úpravy).</summary>
    public Trip? Trip { get; private set; }

    public TripForm Form { get; private set; } = new();

    public IReadOnlyList<string> TripPhotos { get; private set; } = [];

    /// <summary>Statistiky uložené trasy (jen u úpravy výletu s trasou).</summary>
    public TrackStats? Stats { get; private set; }

    /// <summary>Chyba nad formulářem (červený box).</summary>
    public string? Error { get; private set; }

    public string MapyApiKey => configuration["Mapy:ApiKey"] ?? "";

    public bool StravaConfigured =>
        !string.IsNullOrEmpty(configuration["Strava:ClientId"]) && !string.IsNullOrEmpty(configuration["Strava:ClientSecret"]);

    public string TripUrl => "/trips/trip/" + Uri.EscapeDataString(Trip?.Id ?? "");

    public async Task<IActionResult> OnGetAsync(string? id)
    {
        if (!await LoadAsync(id))
            return TripNotFound();

        Form = Trip is null ? new TripForm() : TripForm.FromTrip(Trip);
        return await ShowAsync();
    }

    public async Task<IActionResult> OnPostAsync(string? id)
    {
        if (!await LoadAsync(id))
            return TripNotFound();

        var form = TripForm.FromRequest(await Request.ReadFormAsync());
        Form = form;
        if (!form.Validate())
        {
            logger.LogWarning("Trip form validation failed for {TripId}", id ?? "(new)");
            return await ShowAsync();
        }

        var data = form.ToData();

        // GPX se rozparsuje před zápisem, aby se jeho hodnoty daly zapsat jedním zápisem s ostatními poli.
        byte[]? gpxBytes = null;
        ParsedTrack? parsed = null;
        var fills = new List<FieldDiff>();
        var conflicts = new List<FieldDiff>();
        if (form.GpxFile is { } gpxFile)
        {
            using (var buffer = new MemoryStream())
            {
                await gpxFile.CopyToAsync(buffer);
                gpxBytes = buffer.ToArray();
            }

            try
            {
                parsed = GpxParser.Parse(gpxBytes);
            }
            catch (GpxParseError ex)
            {
                logger.LogWarning(ex, "Invalid GPX upload");
                Error = GpxErrorMessage(ex);
                return await ShowAsync();
            }

            data = data with { TrackJson = parsed.Stats.ToJson() };

            // Prázdná pole se vyplní hned, cokoli by přepsalo zadanou hodnotu čeká na potvrzení.
            var derived = TrackFieldComparer.Derived(parsed.Stats);
            foreach (var diff in TrackFieldComparer.Compare(derived, form.ToComparable()))
                (diff.Current is null ? fills : conflicts).Add(diff);
            foreach (var fill in fills)
                data = data.WithDerived(fill.Field, derived[fill.Field]);
        }

        string tripId;
        try
        {
            if (IsNew)
            {
                tripId = await trips.CreateAsync(data);
            }
            else
            {
                tripId = id!;
                await trips.UpdateAsync(tripId, data);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error saving trip {TripId}", id ?? "(new)");
            Error = UserErrors.Message(environment, IsNew ? "Error creating trip" : "Error updating trip", ex);
            return await ShowAsync();
        }

        if (parsed is not null)
        {
            // Uložení je dvojí zápis (GPX, pak GeoJSON) bez transakce; při chybě mezi nimi zůstane jen GPX.
            try
            {
                await tracks.SaveAsync(tripId, gpxBytes!, Encoding.UTF8.GetBytes(parsed.GeoJson));
                TempData.AddMessage("success",
                    $"Trasa nahrána: {DjangoFormat.Float(parsed.Stats.DistanceKm)} km, ↑{parsed.Stats.MetersAscend} m.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error uploading track for {TripId}", tripId);
                TempData.AddMessage("error", UserErrors.Message(environment, "Trasu se nepodařilo uložit", ex));
            }
        }

        foreach (var photo in form.Photos)
        {
            try
            {
                await using var stream = photo.OpenReadStream();
                await photos.UploadAsync(tripId, stream, photo.FileName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fotka, která nejde zpracovat, nesmí shodit uložení výletu.
                logger.LogWarning(ex, "Error uploading photo {FileName} for {TripId}", photo.FileName, tripId);
                TempData.AddMessage("warning", $"Fotku {photo.FileName} se nepodařilo nahrát.");
            }
        }

        var encodedId = Uri.EscapeDataString(tripId);
        if (conflicts.Count > 0)
        {
            if (fills.Count > 0)
                TempData.AddMessage("info", $"Prázdná pole ({fills.Count}) jsou předvyplněná z GPX.");
            return Redirect($"/trips/trip/{encodedId}/track/apply/");
        }

        // Hodnoty z GPX jsou jen výchozí bod (vrchol je nejvyšší souřadnice, ne vždy ten správný), proto zůstaneme v editoru.
        if (fills.Count > 0)
        {
            TempData.AddMessage("info",
                "Prázdná pole jsou předvyplněná z GPX. Můžeš je upravit (vrchol přetažením značky na mapě) a uložit znovu.");
            return Redirect($"/trips/trip/{encodedId}/edit/");
        }

        return Redirect($"/trips/trip/{encodedId}/");
    }

    // ----- pomocníci pro šablonu -----

    /// <summary><c>value="…"</c> jen pro neprázdnou hodnotu (jako widgety v Django).</summary>
    public static IHtmlContent ValueAttr(string value) =>
        value.Length == 0 ? HtmlString.Empty : new HtmlString($" value=\"{Escape(value)}\"");

    /// <summary><c>&lt;ul class="errorlist"&gt;</c> bez mezer mezi značkami, jako <c>{{ field.errors }}</c>.</summary>
    public IHtmlContent ErrorList(string field)
    {
        var errors = Form.ErrorsFor(field);
        if (errors.Count == 0)
            return HtmlString.Empty;
        return new HtmlString("<ul class=\"errorlist\">"
            + string.Concat(errors.Select(e => "<li>" + Escape(e) + "</li>")) + "</ul>");
    }

    public static IHtmlContent Select(string name, string selected, IReadOnlyList<(string Value, string Label)> choices)
    {
        var html = new StringBuilder();
        html.Append($"<select name=\"{name}\" class=\"form-control\" id=\"id_{name}\">");
        foreach (var (value, label) in choices)
        {
            var isSelected = selected.Length > 0 && selected == value;
            html.Append($"\n<option value=\"{Escape(value)}\"{(isSelected ? " selected" : "")}>{Escape(label)}</option>");
        }

        html.Append("\n</select>");
        return new HtmlString(html.ToString());
    }

    /// <summary>Jako Django <c>escape</c>: jen <c>&amp; &lt; &gt; " '</c>, háčky a čárky zůstávají čitelné.</summary>
    private static string Escape(string text) => text
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#x27;");

    private async Task<bool> LoadAsync(string? id)
    {
        IsNew = id is null;
        if (IsNew)
            return true;

        Trip = await trips.GetAsync(id!);
        if (Trip is null)
            logger.LogWarning("Trip with ID {TripId} not found", id);
        return Trip is not null;
    }

    private async Task<IActionResult> ShowAsync()
    {
        if (Trip is not null)
        {
            TripPhotos = await photos.ListAsync(Trip.Id);
            Stats = StoredTrackStats.Read(Trip.TrackJson);
        }

        return Page();
    }

    private static IActionResult TripNotFound() => new ContentResult
    {
        Content = "Trip not found",
        ContentType = "text/html; charset=utf-8",
        StatusCode = StatusCodes.Status404NotFound,
    };

    /// <summary>Hlášky parseru jsou určené uživateli; podrobnosti z XML parseru (řádek, pozice) jen ve vývoji.</summary>
    private string GpxErrorMessage(GpxParseError error)
    {
        if (error.InnerException is null || environment.IsDevelopment())
            return error.Message;
        return "Soubor se nepodařilo přečíst jako GPX.";
    }
}
