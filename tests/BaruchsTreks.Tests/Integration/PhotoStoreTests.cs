using BaruchsTreks.Photos;
using Microsoft.Extensions.Options;

namespace BaruchsTreks.Tests.Integration;

public class PhotoStoreTests(AzuriteFixture azurite)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static (TestStorage Storage, PhotoStore Store) Create(AzuriteFixture azurite, string publicBase = "http://blob.test/")
    {
        var storage = new TestStorage(azurite);
        storage.Options.PublicBlobBaseUrl = publicBase;
        var clock = new FixedClock(new DateTimeOffset(2025, 8, 15, 10, 30, 5, TimeSpan.Zero));
        return (storage, new PhotoStore(storage.Blobs, Options.Create(storage.Options), clock));
    }

    [Fact]
    public async Task Upload_NamesBlobWithTimestamp()
    {
        var (storage, store) = Create(azurite);
        var png = TestImages.Png(8, 8);

        var url = await store.UploadAsync("trip_1", new MemoryStream(png), "holiday.PNG");

        Assert.Equal($"http://blob.test/{storage.Options.PhotosContainer}/trip_1/20250815103005.PNG", url);
        var blob = storage.Blobs.GetBlobContainerClient(storage.Options.PhotosContainer)
            .GetBlobClient("trip_1/20250815103005.PNG");
        var props = await blob.GetPropertiesAsync(cancellationToken: Ct);
        Assert.Equal("image/png", props.Value.ContentType);
        Assert.Equal(png.Length, props.Value.ContentLength);
    }

    [Fact]
    public async Task Upload_Heic_StoresJpeg()
    {
        var (storage, store) = Create(azurite);

        var url = await store.UploadAsync("trip_1", new MemoryStream(TestImages.Heic()), "IMG_1.heic");

        Assert.EndsWith("/trip_1/20250815103005.jpg", url);
        var blob = storage.Blobs.GetBlobContainerClient(storage.Options.PhotosContainer)
            .GetBlobClient("trip_1/20250815103005.jpg");
        Assert.Equal("image/jpeg", (await blob.GetPropertiesAsync(cancellationToken: Ct)).Value.ContentType);
    }

    [Fact]
    public async Task Upload_Corrupt_ThrowsAndStoresNothing()
    {
        var (_, store) = Create(azurite);

        await Assert.ThrowsAsync<PhotoException>(() =>
            store.UploadAsync("trip_1", new MemoryStream("junk"u8.ToArray()), "x.heic"));
        Assert.Empty(await store.ListAsync("trip_1"));
    }

    [Fact]
    public async Task List_ReturnsPublicUrls()
    {
        var (storage, store) = Create(azurite);
        await store.UploadAsync("trip_1", new MemoryStream(TestImages.Png(4, 4)), "a.png");
        await store.UploadAsync("trip_2", new MemoryStream(TestImages.Png(4, 4)), "b.png");

        var urls = await store.ListAsync("trip_1");

        Assert.Equal([$"http://blob.test/{storage.Options.PhotosContainer}/trip_1/20250815103005.png"], urls);
        Assert.Empty(await store.ListAsync("trip_none"));
    }

    [Fact]
    public async Task List_WithoutPublicBase_UsesContainerAddress()
    {
        var (storage, store) = Create(azurite, publicBase: "");
        await store.UploadAsync("trip_1", new MemoryStream(TestImages.Png(4, 4)), "a.png");

        var url = Assert.Single(await store.ListAsync("trip_1"));

        Assert.StartsWith(storage.Blobs.GetBlobContainerClient(storage.Options.PhotosContainer).Uri.AbsoluteUri, url);
    }

    [Fact]
    public async Task DeleteByUrl_RemovesBlob()
    {
        var (storage, store) = Create(azurite);
        var url = await store.UploadAsync("trip_1", new MemoryStream(TestImages.Png(4, 4)), "a.png");

        await store.DeleteByUrlAsync(url);

        Assert.Empty(await store.ListAsync("trip_1"));
        var container = storage.Blobs.GetBlobContainerClient(storage.Options.PhotosContainer);
        Assert.False((await container.GetBlobClient("trip_1/20250815103005.png").ExistsAsync(Ct)).Value);
    }

    [Fact]
    public async Task DeleteByUrl_ForeignOrMissing_IsIgnored()
    {
        var (_, store) = Create(azurite);
        await store.UploadAsync("trip_1", new MemoryStream(TestImages.Png(4, 4)), "a.png");

        await store.DeleteByUrlAsync("https://evil.example/photos/trip_1/20250815103005.png");
        await store.DeleteByUrlAsync("http://blob.test/other/trip_1/20250815103005.png");
        await store.DeleteByUrlAsync("");

        Assert.Single(await store.ListAsync("trip_1"));
    }

    [Fact]
    public async Task DeleteByUrl_MissingBlob_IsNotAnError()
    {
        var (storage, store) = Create(azurite);
        await store.UploadAsync("trip_1", new MemoryStream(TestImages.Png(4, 4)), "a.png");

        await store.DeleteByUrlAsync($"http://blob.test/{storage.Options.PhotosContainer}/trip_1/nonexistent.png");

        Assert.Single(await store.ListAsync("trip_1"));
    }

    [Fact]
    public async Task Upload_SameSecondSameExtension_NeverOverwrites()
    {
        // Pevné hodiny: všechny tři nahrání spadnou do téže sekundy.
        var (storage, store) = Create(azurite);

        byte[][] sources = [TestImages.Png(4, 4), TestImages.Jpeg(5, 5), TestImages.Png(6, 20)];
        var first = await store.UploadAsync("trip_1", new MemoryStream(sources[0]), "a.png");
        var second = await store.UploadAsync("trip_1", new MemoryStream(sources[1]), "b.png");
        var third = await store.UploadAsync("trip_1", new MemoryStream(sources[2]), "c.png");

        Assert.Equal(3, new[] { first, second, third }.Distinct().Count());
        var listed = await store.ListAsync("trip_1");
        Assert.Equal(3, listed.Count);
        Assert.EndsWith("/trip_1/20250815103005.png", first);
        Assert.All(listed, url => Assert.Matches(@"/trip_1/\d{14}\.png$", url));
        // Každá fotka zůstala svá: obsah bloků je přesně to, co se nahrálo.
        var container = storage.Blobs.GetBlobContainerClient(storage.Options.PhotosContainer);
        var stored = new List<string>();
        foreach (var url in listed)
        {
            var name = "trip_1/" + url[(url.LastIndexOf('/') + 1)..];
            var content = (await container.GetBlobClient(name).DownloadContentAsync(Ct)).Value.Content.ToArray();
            stored.Add(Convert.ToBase64String(content));
        }

        Assert.Equal(sources.Select(Convert.ToBase64String).Order(), stored.Order());
    }
}
