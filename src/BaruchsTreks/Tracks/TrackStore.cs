using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using BaruchsTreks.Storage;
using Microsoft.Extensions.Options;

namespace BaruchsTreks.Tracks;

public interface ITrackStore
{
    /// <summary>Uloží původní GPX i zjednodušený GeoJSON (přepíše existující). Kontejner se vytvoří při prvním zápisu.</summary>
    Task SaveAsync(string tripId, byte[] gpx, byte[] geojson);

    /// <summary>Původní GPX, nebo <c>null</c> (výlet nemá trasu).</summary>
    Task<byte[]?> GetGpxAsync(string tripId);

    /// <summary>Zjednodušený GeoJSON, nebo <c>null</c>.</summary>
    Task<byte[]?> GetGeoJsonAsync(string tripId);

    /// <summary>Smaže oba bloby; chybějící jsou v pořádku.</summary>
    Task DeleteAsync(string tripId);
}

/// <summary>Trasy v kontejneru <c>tracks</c>: <c>{tripId}/track.gpx</c> a <c>{tripId}/track.geojson</c>.</summary>
public sealed class TrackStore(BlobServiceClient blobs, IOptions<StorageOptions> options) : ITrackStore
{
    public const string GpxBlob = "track.gpx";
    public const string GeoJsonBlob = "track.geojson";
    public const string GpxContentType = "application/gpx+xml";
    public const string GeoJsonContentType = "application/geo+json";

    private readonly BlobContainerClient _container = blobs.GetBlobContainerClient(options.Value.TracksContainer);

    public async Task SaveAsync(string tripId, byte[] gpx, byte[] geojson)
    {
        // Existující kontejner se nijak nemění (bootstrapper ho obvykle už vytvořil).
        await _container.CreateIfNotExistsAsync(PublicAccessType.None);
        await UploadAsync(tripId, GpxBlob, gpx, GpxContentType);
        await UploadAsync(tripId, GeoJsonBlob, geojson, GeoJsonContentType);
    }

    public Task<byte[]?> GetGpxAsync(string tripId) => DownloadAsync(tripId, GpxBlob);

    public Task<byte[]?> GetGeoJsonAsync(string tripId) => DownloadAsync(tripId, GeoJsonBlob);

    public async Task DeleteAsync(string tripId)
    {
        foreach (var name in new[] { GpxBlob, GeoJsonBlob })
            await _container.GetBlobClient($"{tripId}/{name}").DeleteIfExistsAsync();
    }

    private async Task UploadAsync(string tripId, string name, byte[] payload, string contentType)
    {
        await _container.GetBlobClient($"{tripId}/{name}").UploadAsync(
            BinaryData.FromBytes(payload),
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } });
    }

    private async Task<byte[]?> DownloadAsync(string tripId, string name)
    {
        try
        {
            var result = await _container.GetBlobClient($"{tripId}/{name}").DownloadContentAsync();
            return result.Value.Content.ToArray();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Chybí blob nebo celý kontejner.
            return null;
        }
    }
}
