using Azure.Data.Tables;
using Azure.Storage.Blobs;
using BaruchsTreks.Storage;
using BaruchsTreks.Trips;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace BaruchsTreks.Tests;

/// <summary>Pomocník pro integrační testy: unikátní jména tabulek a kontejnerů nad sdíleným Azurite.</summary>
public sealed class TestStorage
{
    public TableServiceClient Tables { get; }
    public BlobServiceClient Blobs { get; }
    public StorageOptions Options { get; }

    public TestStorage(AzuriteFixture azurite)
    {
        Tables = new TableServiceClient(azurite.ConnectionString);
        Blobs = new BlobServiceClient(azurite.ConnectionString);
        Options = new StorageOptions
        {
            TripsTable = "Trips" + Hex(),
            UsersTable = "Users" + Hex(),
            PhotosContainer = "photos-" + Hex(),
            TracksContainer = "tracks-" + Hex(),
            KeysContainer = "keys-" + Hex(),
        };
    }

    public TableClient UsersTableClient => Tables.GetTableClient(Options.UsersTable);

    public TableClient TripsTableClient => Tables.GetTableClient(Options.TripsTable);

    public StorageBootstrapper Bootstrapper(bool development) =>
        new(Tables, Blobs, Microsoft.Extensions.Options.Options.Create(Options), new FakeEnvironment(development));

    /// <summary>Vytvoří tabulku výletů a vrátí repozitář nad ní.</summary>
    public async Task<TripRepository> CreateRepositoryAsync()
    {
        await TripsTableClient.CreateIfNotExistsAsync(TestContext.Current.CancellationToken);
        return new TripRepository(Tables, Microsoft.Extensions.Options.Options.Create(Options));
    }

    public async Task<ConfigRepository> CreateConfigRepositoryAsync()
    {
        await TripsTableClient.CreateIfNotExistsAsync(TestContext.Current.CancellationToken);
        return new ConfigRepository(Tables, Microsoft.Extensions.Options.Options.Create(Options));
    }

    public async Task<TableEntity> RawAsync(string rowKey) =>
        (await TripsTableClient.GetEntityAsync<TableEntity>("Trips", rowKey,
            cancellationToken: TestContext.Current.CancellationToken)).Value;

    private static string Hex() => Guid.NewGuid().ToString("N")[..8];

    private sealed class FakeEnvironment(bool development) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = development ? Environments.Development : Environments.Production;
        public string ApplicationName { get; set; } = "BaruchsTreks.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
