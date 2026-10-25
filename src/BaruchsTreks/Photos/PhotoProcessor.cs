using ImageMagick;

namespace BaruchsTreks.Photos;

/// <summary>Fotku nelze zpracovat (poškozený nebo nečitelný soubor).</summary>
public sealed class PhotoException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Fotka připravená k uložení; <see cref="Content"/> je nastavený na začátek a patří příjemci.</summary>
public sealed record ProcessedPhoto(Stream Content, string Extension, string ContentType);

/// <summary>
/// Příprava fotky před uložením: HEIC/HEIF se převede na JPEG (kvalita 90, otočení podle EXIF),
/// ostatní formáty zůstanou beze změny. Veškeré použití Magick.NET je jen tady (kvůli SelfTestu v image).
/// </summary>
public static class PhotoProcessor
{
    private const uint JpegQuality = 90;
    private const string OctetStream = "application/octet-stream";

    static PhotoProcessor()
    {
        // Strop paměti pro ImageMagick: velká fotka nesmí na malém VPS shodit proces.
        ResourceLimits.Memory = 200UL * 1024 * 1024;
    }

    public static ProcessedPhoto Prepare(Stream input, string fileName)
    {
        var extension = Path.GetExtension(fileName ?? "");
        byte[] data;
        try
        {
            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            data = buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
        {
            throw new PhotoException("The photo could not be read.", ex);
        }

        try
        {
            // Ping načte jen hlavičku; zjistí skutečný formát a zahodí soubory, které nejsou obrázek.
            using var probe = new MagickImage();
            probe.Ping(data);

            var isHeic = probe.Format is MagickFormat.Heic or MagickFormat.Heif
                || extension.Equals(".heic", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".heif", StringComparison.OrdinalIgnoreCase);
            if (!isHeic)
                return new ProcessedPhoto(new MemoryStream(data, writable: false), extension, ContentTypeOf(extension));

            using var image = new MagickImage(data);
            // Orientaci z EXIF zapéct do pixelů; EXIF orientace se tím vynuluje.
            image.AutoOrient();
            image.Quality = JpegQuality;
            image.Format = MagickFormat.Jpeg;
            return new ProcessedPhoto(new MemoryStream(image.ToByteArray()), ".jpg", "image/jpeg");
        }
        catch (MagickException ex)
        {
            throw new PhotoException("The photo is not a readable image.", ex);
        }
        catch (Exception ex) when (ex is not PhotoException and not OutOfMemoryException)
        {
            throw new PhotoException("The photo could not be processed.", ex);
        }
    }

    /// <summary>Content-Type podle přípony (jako dřív v Pythonu); neznámé je <c>application/octet-stream</c>.</summary>
    public static string ContentTypeOf(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        _ => OctetStream,
    };
}
