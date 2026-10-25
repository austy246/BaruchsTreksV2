using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using BaruchsTreks.Cli;
using BaruchsTreks.Trips;
using Microsoft.Extensions.Configuration;

namespace BaruchsTreks.Tests.Integration;

public class CheckDataTests(AzuriteFixture azurite)
{
    private const string TripA = "trip_20250815100000";
    private const string TripB = "trip_20240701100000";
    private const string TripC = "trip_20240101100000";
    private const string TripD = "trip_20230101100000";
    private const string SecretToken = "SECRET_TOKEN_VALUE";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CheckData_ReadsLegacyEntitiesWithoutWriting()
    {
        var storage = new TestStorage(azurite);
        await SeedAsync(storage);
        var before = await SnapshotAsync(storage);
        var output = new StringWriter();

        var exitCode = await CheckDataCommand.RunAsync(ConfigFor(storage), output, Ct);

        var text = output.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("Trips: 4, unreadable: 0", text);
        Assert.Contains("Config rows: 1, unparsable: 0", text);
        Assert.Contains("Photos: 3", text);
        Assert.Contains("HEIC/HEIF photos (browsers cannot display them): 2", text);
        Assert.Contains($"{TripA}/20250815100001.HEIC", text);
        Assert.Contains($"{TripB}/phone.heif", text);
        Assert.Contains("Tracks: 4 blobs in 3 trips", text);
        Assert.Contains("Orphan tracks (no trip): 1", text);
        Assert.Contains("trip_orphan", text);
        Assert.Contains("Trips with TrackJson but missing track blobs: 2", text);
        Assert.Contains($"{TripC} (missing track.gpx, track.geojson)", text);
        Assert.Contains($"{TripD} (missing track.geojson)", text);
        Assert.Contains("LengthHours: Int32 1, Int64 0, Double 3, missing 0, other 0", text);
        Assert.Contains("ElevationGain: Int32 1, Int64 1, Double 0, missing 2, other 0", text);
        Assert.Contains("MetersAscend: Int32 1, Int64 0, Double 1, missing 2, other 0", text);
        Assert.Contains("MetersDescend: Int32 0, Int64 1, Double 0, missing 3, other 0", text);
        Assert.Contains("Storage account: devstoreaccount1", text);
        Assert.DoesNotContain("AccountKey", text);
        Assert.DoesNotContain(SecretToken, text);

        // Nic nového a nic změněného: stejné entity i bloby se stejnými ETagy, žádná tabulka ani kontejner navíc.
        Assert.Equal(before, await SnapshotAsync(storage));
        Assert.False(await TableExists(storage, storage.Options.UsersTable));
        Assert.False(await storage.Blobs.GetBlobContainerClient(storage.Options.KeysContainer).ExistsAsync(Ct));
    }

    [Fact]
    public async Task CheckData_MissingContainers_AreReportedAndNotCreated()
    {
        var storage = new TestStorage(azurite);
        await storage.TripsTableClient.CreateIfNotExistsAsync(Ct);
        await storage.TripsTableClient.AddEntityAsync(LegacyEntities.A(TripA), Ct);
        var output = new StringWriter();

        var exitCode = await CheckDataCommand.RunAsync(ConfigFor(storage), output, Ct);

        var text = output.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("Photos: 0 (container not found)", text);
        Assert.Contains("Tracks: 0 blobs in 0 trips (container not found)", text);
        Assert.False(await storage.Blobs.GetBlobContainerClient(storage.Options.PhotosContainer).ExistsAsync(Ct));
        Assert.False(await storage.Blobs.GetBlobContainerClient(storage.Options.TracksContainer).ExistsAsync(Ct));
        Assert.False(await TableExists(storage, storage.Options.UsersTable));
    }

    [Fact]
    public async Task CheckData_MissingTripsTable_FailsWithoutCreating()
    {
        var storage = new TestStorage(azurite);
        var output = new StringWriter();

        var exitCode = await CheckDataCommand.RunAsync(ConfigFor(storage), output, Ct);

        Assert.Equal(1, exitCode);
        Assert.Contains($"Table {storage.Options.TripsTable} not found", output.ToString());
        Assert.False(await TableExists(storage, storage.Options.TripsTable));
    }

    [Fact]
    public async Task CheckData_UnreadableEntity_ExitsNonZero()
    {
        var storage = new TestStorage(azurite);
        await storage.TripsTableClient.CreateIfNotExistsAsync(Ct);
        await storage.TripsTableClient.AddEntityAsync(LegacyEntities.A(TripA), Ct);
        await storage.TripsTableClient.AddEntityAsync(LegacyEntities.B(TripB), Ct);
        var output = new StringWriter();

        // Skutečná data tolerantní mapper přečte vždy, nečitelnou entitu proto simuluje vlastní převodník.
        var exitCode = await CheckDataCommand.RunAsync(ConfigFor(storage), output, e =>
            e.RowKey == TripB ? throw new FormatException("bad value") : TripEntityMapper.FromEntity(e), Ct);

        var text = output.ToString();
        Assert.Equal(1, exitCode);
        Assert.Contains("Trips: 2, unreadable: 1", text);
        Assert.Contains($"{TripB}: bad value", text);
    }

    [Fact]
    public async Task CheckData_UnparsableConfigRow_ExitsNonZeroWithoutPrintingValue()
    {
        var storage = new TestStorage(azurite);
        await storage.TripsTableClient.CreateIfNotExistsAsync(Ct);
        await storage.TripsTableClient.AddEntityAsync(
            new TableEntity("Config", "strava_tokens_bad") { ["Value"] = $"{{not json {SecretToken}" }, Ct);
        var output = new StringWriter();

        var exitCode = await CheckDataCommand.RunAsync(ConfigFor(storage), output, Ct);

        var text = output.ToString();
        Assert.Equal(1, exitCode);
        Assert.Contains("Config rows: 1, unparsable: 1", text);
        Assert.Contains("strava_tokens_bad", text);
        Assert.DoesNotContain(SecretToken, text);
    }

    [Fact]
    public async Task CheckData_MissingConnectionString_FailsWithMessage()
    {
        var output = new StringWriter();
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var exitCode = await CheckDataCommand.RunAsync(config, output, Ct);

        Assert.Equal(1, exitCode);
        Assert.Contains("ConnectionStrings:Storage", output.ToString());
    }

    [Fact]
    public async Task CheckData_UnreachableStorage_FailsWithoutLeakingConnectionString()
    {
        var output = new StringWriter();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Storage"] =
                "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=c2VjcmV0LWtleS1mb3ItdGVzdA==;"
                + "BlobEndpoint=http://127.0.0.1:1/devstoreaccount1;TableEndpoint=http://127.0.0.1:1/devstoreaccount1;",
        }).Build();

        var exitCode = await CheckDataCommand.RunAsync(config, output, Ct);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("c2VjcmV0LWtleS1mb3ItdGVzdA", output.ToString());
    }

    /// <summary>Čtyři výlety, jedna konfigurace, tři fotky (dvě HEIC/HEIF) a čtyři bloby tras (jedna osiřelá).</summary>
    private async Task SeedAsync(TestStorage storage)
    {
        var table = storage.TripsTableClient;
        await table.CreateIfNotExistsAsync(Ct);
        await table.AddEntityAsync(LegacyEntities.A(TripA), Ct);
        await table.AddEntityAsync(LegacyEntities.B(TripB), Ct);
        await table.AddEntityAsync(new TableEntity("Trips", TripC) { ["Title"] = "C", ["LengthHours"] = 2.5, ["TrackJson"] = "{}" }, Ct);
        await table.AddEntityAsync(new TableEntity("Trips", TripD) { ["Title"] = "D", ["LengthHours"] = 3.5, ["TrackJson"] = "{}" }, Ct);
        await table.AddEntityAsync(
            new TableEntity("Config", "strava_tokens_austy") { ["Value"] = $$"""{"access_token":"{{SecretToken}}","expires_at":1}""" }, Ct);

        var photos = storage.Blobs.GetBlobContainerClient(storage.Options.PhotosContainer);
        await photos.CreateIfNotExistsAsync(cancellationToken: Ct);
        await UploadAsync(photos, $"{TripA}/20250815100000.jpg", "image/jpeg");
        await UploadAsync(photos, $"{TripA}/20250815100001.HEIC", "application/octet-stream");
        await UploadAsync(photos, $"{TripB}/phone.heif", "image/heif");

        var tracks = storage.Blobs.GetBlobContainerClient(storage.Options.TracksContainer);
        await tracks.CreateIfNotExistsAsync(cancellationToken: Ct);
        await UploadAsync(tracks, $"{TripB}/track.gpx", "application/gpx+xml");
        await UploadAsync(tracks, $"{TripB}/track.geojson", "application/geo+json");
        await UploadAsync(tracks, $"{TripD}/track.gpx", "application/gpx+xml");
        await UploadAsync(tracks, "trip_orphan/track.gpx", "application/gpx+xml");
    }

    private static Task UploadAsync(BlobContainerClient container, string name, string contentType) =>
        container.GetBlobClient(name).UploadAsync(
            BinaryData.FromString("x"), new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, Ct);

    private IConfiguration ConfigFor(TestStorage storage) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Storage"] = azurite.ConnectionString,
            ["Storage:TripsTable"] = storage.Options.TripsTable,
            ["Storage:UsersTable"] = storage.Options.UsersTable,
            ["Storage:PhotosContainer"] = storage.Options.PhotosContainer,
            ["Storage:TracksContainer"] = storage.Options.TracksContainer,
            ["Storage:KeysContainer"] = storage.Options.KeysContainer,
        })
        .Build();

    /// <summary>Všechny entity (klíč + ETag) a bloby (kontejner/jméno + ETag) v prostoru jmen jednoho testu.</summary>
    private static async Task<List<string>> SnapshotAsync(TestStorage storage)
    {
        var lines = new List<string>();
        if (await TableExists(storage, storage.Options.TripsTable))
        {
            await foreach (var e in storage.TripsTableClient.QueryAsync<TableEntity>(cancellationToken: Ct))
                lines.Add($"table {e.PartitionKey}/{e.RowKey} {e.ETag}");
        }
        foreach (var name in new[] { storage.Options.PhotosContainer, storage.Options.TracksContainer })
        {
            var container = storage.Blobs.GetBlobContainerClient(name);
            if (!await container.ExistsAsync(Ct))
                continue;
            lines.Add($"container {name}");
            await foreach (var b in container.GetBlobsAsync(cancellationToken: Ct))
                lines.Add($"blob {name}/{b.Name} {b.Properties.ETag}");
        }
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static async Task<bool> TableExists(TestStorage storage, string name)
    {
        await foreach (var _ in storage.Tables.QueryAsync(t => t.Name == name, cancellationToken: Ct))
            return true;
        return false;
    }
}
