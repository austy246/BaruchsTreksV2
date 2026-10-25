using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using BaruchsTreks.Storage;
using Microsoft.Extensions.Options;

namespace BaruchsTreks.Photos;

public interface IPhotoStore
{
    /// <summary>Uloží fotku jako <c>{tripId}/{yyyyMMddHHmmss}{přípona}</c> a vrátí její veřejnou URL.</summary>
    Task<string> UploadAsync(string tripId, Stream content, string fileName);

    /// <summary>Veřejné URL všech fotek výletu (pořadí podle jména bloku).</summary>
    Task<IReadOnlyList<string>> ListAsync(string tripId);

    /// <summary>Smaže fotku podle URL. URL mimo kontejner fotek se ignoruje; neexistující blob je v pořádku.</summary>
    Task DeleteByUrlAsync(string url);
}

/// <summary>
/// Fotky v kontejneru <c>photos</c>. Veřejné URL se skládají z <c>Storage:PublicBlobBaseUrl</c>
/// (adresa, kterou vidí prohlížeč), jinak z adresy kontejneru, na kterou se připojuje aplikace.
/// </summary>
public sealed class PhotoStore(BlobServiceClient blobs, IOptions<StorageOptions> options, TimeProvider? clock = null)
    : IPhotoStore
{
    private readonly StorageOptions _options = options.Value;
    private readonly BlobContainerClient _container = blobs.GetBlobContainerClient(options.Value.PhotosContainer);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<string> UploadAsync(string tripId, Stream content, string fileName)
    {
        // Převod (HEIC na JPEG) i kontrola obrázku proběhnou před jakýmkoli zápisem do storage.
        var photo = PhotoProcessor.Prepare(content, fileName);
        using var photoContent = photo.Content;

        var timestamp = _clock.GetLocalNow().ToString("yyyyMMddHHmmss");
        var blobName = $"{tripId}/{timestamp}{photo.Extension}";

        // Existující kontejner se nijak nemění (bootstrapper ho obvykle už vytvořil).
        await _container.CreateIfNotExistsAsync(PublicAccessType.None);
        await _container.GetBlobClient(blobName).UploadAsync(
            photoContent,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = photo.ContentType } });
        return PublicUrl(blobName);
    }

    public async Task<IReadOnlyList<string>> ListAsync(string tripId)
    {
        var urls = new List<string>();
        try
        {
            await foreach (var item in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, $"{tripId}/", CancellationToken.None))
                urls.Add(PublicUrl(item.Name));
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Kontejner ještě neexistuje.
        }

        return urls;
    }

    public async Task DeleteByUrlAsync(string url)
    {
        var blobName = BlobNameFromUrl(url);
        if (blobName is null)
            return;
        await _container.GetBlobClient(blobName).DeleteIfExistsAsync();
    }

    private string PublicUrl(string blobName)
    {
        var path = string.Join('/', blobName.Split('/').Select(Uri.EscapeDataString));
        var prefix = _options.PublicBlobBaseUrl.Length > 0
            ? $"{_options.PublicBlobBaseUrl}{_options.PhotosContainer}/"
            : _container.Uri.AbsoluteUri.TrimEnd('/') + "/";
        return prefix + path;
    }

    /// <summary>Jméno bloku z URL; <c>null</c>, když URL nepatří do kontejneru fotek.</summary>
    private string? BlobNameFromUrl(string url)
    {
        var prefixes = new List<string>();
        if (_options.PublicBlobBaseUrl.Length > 0)
            prefixes.Add($"{_options.PublicBlobBaseUrl}{_options.PhotosContainer}/");
        prefixes.Add(_container.Uri.AbsoluteUri.TrimEnd('/') + "/");

        foreach (var prefix in prefixes)
        {
            if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            // Bez dotazu a fragmentu (např. SAS token ve starých URL).
            var rest = url[prefix.Length..];
            var cut = rest.IndexOfAny(['?', '#']);
            if (cut >= 0)
                rest = rest[..cut];
            var name = Uri.UnescapeDataString(rest);
            return name.Length > 0 ? name : null;
        }

        return null;
    }
}
