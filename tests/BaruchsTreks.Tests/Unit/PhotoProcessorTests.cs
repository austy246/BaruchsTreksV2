using BaruchsTreks.Photos;
using ImageMagick;

namespace BaruchsTreks.Tests.Unit;

public class PhotoProcessorTests
{
    private static (uint W, uint H, MagickFormat Format, Orientation Orientation) Describe(Stream s)
    {
        var info = new MagickImageInfo(s);
        return (info.Width, info.Height, info.Format, info.Orientation);
    }

    [Fact]
    public void Prepare_Heic_ReturnsJpeg()
    {
        var p = PhotoProcessor.Prepare(new MemoryStream(TestImages.Heic()), "IMG_0001.HEIC");
        using var content = p.Content;

        Assert.Equal(".jpg", p.Extension);
        Assert.Equal("image/jpeg", p.ContentType);
        content.Position = 0;
        Assert.Equal(MagickFormat.Jpeg, Describe(content).Format);
    }

    [Fact]
    public void Prepare_Jpeg_IsUnchanged()
    {
        var bytes = TestImages.Jpeg(40, 20);
        var p = PhotoProcessor.Prepare(new MemoryStream(bytes), "a.jpeg");
        using var content = p.Content;

        Assert.Equal(".jpeg", p.Extension);
        Assert.Equal("image/jpeg", p.ContentType);
        Assert.Equal(bytes, ReadAll(content));
    }

    [Fact]
    public void Prepare_Png_KeepsExtensionAndContentType()
    {
        var bytes = TestImages.Png(10, 10);
        var p = PhotoProcessor.Prepare(new MemoryStream(bytes), "Map.PNG");
        using var content = p.Content;

        Assert.Equal(".PNG", p.Extension);
        Assert.Equal("image/png", p.ContentType);
        Assert.Equal(bytes, ReadAll(content));
    }

    [Theory]
    [InlineData("x.png")]
    [InlineData("x.heic")]
    [InlineData("x.jpg")]
    [InlineData("x")]
    public void Prepare_Corrupt_ThrowsPhotoException(string name)
    {
        Assert.Throws<PhotoException>(() => PhotoProcessor.Prepare(new MemoryStream("not an image"u8.ToArray()), name));
    }

    [Fact]
    public void Prepare_TruncatedHeic_ThrowsPhotoException()
    {
        var truncated = TestImages.Heic()[..5000];
        Assert.Throws<PhotoException>(() => PhotoProcessor.Prepare(new MemoryStream(truncated), "a.heic"));
    }

    [Fact]
    public void Prepare_ExifRotated_IsOriented()
    {
        // Soubor s příponou .heic, ale JPEG obsahem s orientací 6 (90 stupňů): výstup je 30x60 bez orientace.
        // (Magick.NET neumí HEIC kódovat, takže orientovaný HEIC nejde vyrobit; AutoOrient je stejná cesta.)
        var p = PhotoProcessor.Prepare(new MemoryStream(TestImages.Jpeg(60, 30, orientation: 6)), "r.heic");
        using var content = p.Content;
        using var image = new MagickImage(content);

        Assert.Equal(Orientation.TopLeft, image.Orientation);
        Assert.Equal((30u, 60u), (image.Width, image.Height));
    }

    [Fact]
    public void Prepare_HeicSample_IsPortraitAndUpright()
    {
        // Vzorek z iPhonu je na výšku 3000x4000; po převodu zůstane stejný rozměr a orientace TopLeft.
        var p = PhotoProcessor.Prepare(new MemoryStream(TestImages.Heic()), "x.heic");
        using var content = p.Content;
        using var image = new MagickImage(content);

        Assert.Equal(Orientation.TopLeft, image.Orientation);
        Assert.Equal((3000u, 4000u), (image.Width, image.Height));
    }

    [Fact]
    public void Prepare_UnknownExtension_OctetStream()
    {
        var p = PhotoProcessor.Prepare(new MemoryStream(TestImages.Png(4, 4)), "x.tiff");
        using var content = p.Content;
        Assert.Equal(".tiff", p.Extension);
        Assert.Equal("application/octet-stream", p.ContentType);
    }

    private static byte[] ReadAll(Stream s)
    {
        s.Position = 0;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
