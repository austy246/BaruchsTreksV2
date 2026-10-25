using ImageMagick;

namespace BaruchsTreks.Tests;

/// <summary>Malé testovací obrázky v paměti (bez sítě).</summary>
public static class TestImages
{
    public static byte[] Jpeg(int w, int h, ushort? orientation = null)
    {
        using var image = new MagickImage(MagickColors.SteelBlue, (uint)w, (uint)h);
        image.Format = MagickFormat.Jpeg;
        if (orientation is { } o)
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, o);
            image.Orientation = (Orientation)o;
            image.SetProfile(exif);
        }

        return image.ToByteArray();
    }

    public static byte[] Png(int w, int h)
    {
        using var image = new MagickImage(MagickColors.SteelBlue, (uint)w, (uint)h);
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }

    public static byte[] Heic() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.heic"));
}
