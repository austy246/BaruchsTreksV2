using System.Text;
using BaruchsTreks.Tracks;

namespace BaruchsTreks.Tests.Integration;

public class TrackStoreTests(AzuriteFixture azurite)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (TestStorage Storage, TrackStore Store) Create(AzuriteFixture azurite)
    {
        var storage = new TestStorage(azurite);
        return (storage, new TrackStore(storage.Blobs, Microsoft.Extensions.Options.Options.Create(storage.Options)));
    }

    [Fact]
    public async Task Save_ThenGet_RoundTrips_WithContentTypes()
    {
        var (storage, store) = Create(azurite);
        var gpx = Encoding.UTF8.GetBytes("<gpx/>");
        var geojson = Encoding.UTF8.GetBytes("{\"type\":\"FeatureCollection\"}");

        await store.SaveAsync("trip_1", gpx, geojson);

        Assert.Equal(gpx, await store.GetGpxAsync("trip_1"));
        Assert.Equal(geojson, await store.GetGeoJsonAsync("trip_1"));
        var container = storage.Blobs.GetBlobContainerClient(storage.Options.TracksContainer);
        var gpxProps = await container.GetBlobClient("trip_1/track.gpx").GetPropertiesAsync(cancellationToken: Ct);
        var jsonProps = await container.GetBlobClient("trip_1/track.geojson").GetPropertiesAsync(cancellationToken: Ct);
        Assert.Equal("application/gpx+xml", gpxProps.Value.ContentType);
        Assert.Equal("application/geo+json", jsonProps.Value.ContentType);
    }

    [Fact]
    public async Task Save_Twice_Overwrites()
    {
        var (_, store) = Create(azurite);
        await store.SaveAsync("trip_1", [1], [2]);
        await store.SaveAsync("trip_1", [3], [4]);

        Assert.Equal(new byte[] { 3 }, await store.GetGpxAsync("trip_1"));
        Assert.Equal(new byte[] { 4 }, await store.GetGeoJsonAsync("trip_1"));
    }

    [Fact]
    public async Task Get_Missing_ReturnsNull()
    {
        var (_, store) = Create(azurite);

        // Kontejner ještě neexistuje (vytvoří se až prvním zápisem).
        Assert.Null(await store.GetGpxAsync("trip_none"));
        Assert.Null(await store.GetGeoJsonAsync("trip_none"));

        await store.SaveAsync("trip_1", [1], [2]);
        Assert.Null(await store.GetGpxAsync("trip_none"));
        Assert.Null(await store.GetGeoJsonAsync("does-not-exist"));
    }

    [Fact]
    public async Task Delete_RemovesBoth()
    {
        var (_, store) = Create(azurite);
        await store.SaveAsync("trip_1", [1], [2]);
        await store.SaveAsync("trip_2", [5], [6]);

        await store.DeleteAsync("trip_1");

        Assert.Null(await store.GetGpxAsync("trip_1"));
        Assert.Null(await store.GetGeoJsonAsync("trip_1"));
        Assert.NotNull(await store.GetGpxAsync("trip_2"));
    }

    [Fact]
    public async Task Delete_Missing_IsNotAnError()
    {
        var (_, store) = Create(azurite);

        await store.DeleteAsync("trip_none");
        await store.SaveAsync("trip_1", [1], [2]);
        await store.DeleteAsync("trip_none");
    }
}
