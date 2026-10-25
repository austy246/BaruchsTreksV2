using Azure.Storage.Blobs.Models;

namespace BaruchsTreks.Tests.Integration;

public class StorageBootstrapperTests(AzuriteFixture azurite)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Bootstrapper_CreatesMissing_Idempotent()
    {
        var storage = new TestStorage(azurite);
        var bootstrapper = storage.Bootstrapper(development: false);

        await bootstrapper.EnsureAsync(Ct);
        await bootstrapper.EnsureAsync(Ct);

        await AssertTableExists(storage, storage.Options.TripsTable);
        await AssertTableExists(storage, storage.Options.UsersTable);
        foreach (var name in new[] { storage.Options.PhotosContainer, storage.Options.TracksContainer, storage.Options.KeysContainer })
            Assert.True(await storage.Blobs.GetBlobContainerClient(name).ExistsAsync(Ct), name);
    }

    [Fact]
    public async Task Bootstrapper_Production_KeepsAllContainersPrivate()
    {
        var storage = new TestStorage(azurite);

        await storage.Bootstrapper(development: false).EnsureAsync(Ct);

        Assert.Equal(PublicAccessType.None, await AccessOf(storage, storage.Options.PhotosContainer));
        Assert.Equal(PublicAccessType.None, await AccessOf(storage, storage.Options.TracksContainer));
        Assert.Equal(PublicAccessType.None, await AccessOf(storage, storage.Options.KeysContainer));
    }

    [Fact]
    public async Task Bootstrapper_Development_MakesOnlyNewPhotosPublic()
    {
        var storage = new TestStorage(azurite);

        await storage.Bootstrapper(development: true).EnsureAsync(Ct);

        Assert.Equal(PublicAccessType.Blob, await AccessOf(storage, storage.Options.PhotosContainer));
        Assert.Equal(PublicAccessType.None, await AccessOf(storage, storage.Options.TracksContainer));
        Assert.Equal(PublicAccessType.None, await AccessOf(storage, storage.Options.KeysContainer));
    }

    [Fact]
    public async Task Bootstrapper_CreatesMissing_NeverChangesExistingContainerAccess()
    {
        var storage = new TestStorage(azurite);
        await storage.Blobs.GetBlobContainerClient(storage.Options.PhotosContainer)
            .CreateAsync(PublicAccessType.None, cancellationToken: Ct);
        await storage.Blobs.GetBlobContainerClient(storage.Options.TracksContainer)
            .CreateAsync(PublicAccessType.Blob, cancellationToken: Ct);

        await storage.Bootstrapper(development: true).EnsureAsync(Ct);
        await storage.Bootstrapper(development: false).EnsureAsync(Ct);

        Assert.Equal(PublicAccessType.None, await AccessOf(storage, storage.Options.PhotosContainer));
        Assert.Equal(PublicAccessType.Blob, await AccessOf(storage, storage.Options.TracksContainer));
    }

    [Fact]
    public async Task Bootstrapper_DoesNotTouchExistingTableData()
    {
        var storage = new TestStorage(azurite);
        await storage.TripsTableClient.CreateIfNotExistsAsync(Ct);
        await storage.TripsTableClient.AddEntityAsync(LegacyEntities.A("trip_20250101000000"), Ct);

        await storage.Bootstrapper(development: false).EnsureAsync(Ct);

        Assert.Equal("Legacy A", (await storage.RawAsync("trip_20250101000000"))["Title"]);
    }

    [Fact]
    public async Task App_Startup_CreatesResourcesWithConfiguredNames()
    {
        await using var factory = new AppFactory(azurite);

        using var client = factory.CreateClient();
        await client.GetAsync("/healthz", Ct);

        var storage = new TestStorage(azurite);
        await AssertTableExists(storage, factory.TripsTable);
        await AssertTableExists(storage, factory.UsersTable);
        foreach (var name in new[] { factory.PhotosContainer, factory.TracksContainer, factory.KeysContainer })
            Assert.True(await storage.Blobs.GetBlobContainerClient(name).ExistsAsync(Ct), name);
        // Test host běží v Development.
        Assert.Equal(PublicAccessType.Blob, await AccessOf(storage, factory.PhotosContainer));
        Assert.Equal(PublicAccessType.None, await AccessOf(storage, factory.KeysContainer));
    }

    private static async Task AssertTableExists(TestStorage storage, string name)
    {
        var names = new List<string>();
        await foreach (var t in storage.Tables.QueryAsync(t => t.Name == name, cancellationToken: Ct))
            names.Add(t.Name);
        Assert.Contains(name, names);
    }

    private static async Task<PublicAccessType?> AccessOf(TestStorage storage, string container)
    {
        var props = await storage.Blobs.GetBlobContainerClient(container).GetPropertiesAsync(cancellationToken: Ct);
        return props.Value.PublicAccess ?? PublicAccessType.None;
    }
}
