using System.Text;
using BaruchsTreks.Storage;
using Microsoft.Net.Http.Headers;

namespace BaruchsTreks.Tracks;

/// <summary>
/// Veřejné stažení trasy výletu. Trasy se předávají přes aplikaci, takže kontejner nepotřebuje veřejné čtení
/// ani CORS. Čtení je pro všechny, bez přihlášení.
/// </summary>
public static class TrackEndpoints
{
    private const string NoTrackJson = "{\"error\": \"No track for this trip\"}";
    private const string NoTrackText = "No track for this trip";

    public static IEndpointRouteBuilder MapTrackEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/trips/trip/{id}/track.geojson", async (string id, ITrackStore tracks, HttpContext http) =>
        {
            var payload = TableKey.IsValid(id) ? await tracks.GetGeoJsonAsync(id) : null;
            if (payload is null)
                return Results.Content(NoTrackJson, "application/json", Encoding.UTF8, StatusCodes.Status404NotFound);

            http.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.Bytes(payload, TrackStore.GeoJsonContentType);
        });

        app.MapGet("/trips/trip/{id}/track.gpx", async (string id, ITrackStore tracks, HttpContext http) =>
        {
            var payload = TableKey.IsValid(id) ? await tracks.GetGpxAsync(id) : null;
            if (payload is null)
                return Results.Content(NoTrackText, "text/html", Encoding.UTF8, StatusCodes.Status404NotFound);

            http.Response.Headers.ContentDisposition = AttachmentHeader($"{id}.gpx");
            return Results.Bytes(payload, TrackStore.GpxContentType);
        });

        return app;
    }

    /// <summary><c>attachment; filename="…"</c>; jméno mimo ASCII jde i jako <c>filename*</c> (RFC 6266).</summary>
    private static string AttachmentHeader(string fileName)
    {
        var header = new ContentDispositionHeaderValue("attachment");
        if (fileName.All(c => c is >= ' ' and <= '~'))
        {
            header.FileName = "\"" + fileName.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
        else
        {
            header.FileName = "\"track.gpx\"";
            header.FileNameStar = fileName;
        }

        return header.ToString();
    }
}
