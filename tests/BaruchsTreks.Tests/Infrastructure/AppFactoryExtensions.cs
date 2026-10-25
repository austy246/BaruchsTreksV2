using Azure.Data.Tables;
using Azure.Storage.Blobs;
using BaruchsTreks.Tracks;
using BaruchsTreks.Trips;
using Microsoft.Extensions.DependencyInjection;

namespace BaruchsTreks.Tests;

/// <summary>Zkratky pro čtení a zápis storage, na kterém běží testovaná aplikace.</summary>
public static class AppFactoryExtensions
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static ITripRepository Trips(this AppFactory factory) => factory.Services.GetRequiredService<ITripRepository>();

    public static ITrackStore Tracks(this AppFactory factory) => factory.Services.GetRequiredService<ITrackStore>();

    public static TableClient TripsTableClient(this AppFactory factory) =>
        factory.Services.GetRequiredService<TableServiceClient>().GetTableClient(factory.TripsTable);

    /// <summary>Entita tak, jak je v tabulce (typy vlastností beze změny).</summary>
    public static async Task<TableEntity> RawTripAsync(this AppFactory factory, string id) =>
        (await factory.TripsTableClient().GetEntityAsync<TableEntity>("Trips", id, cancellationToken: Ct)).Value;

    /// <summary>Zapíše entitu přesně takovou, jaká je (např. starý tvar z <see cref="LegacyEntities"/>).</summary>
    public static async Task PutRawTripAsync(this AppFactory factory, TableEntity entity) =>
        await factory.TripsTableClient().UpsertEntityAsync(entity, TableUpdateMode.Replace, Ct);

    /// <summary>Počet bloků v kontejneru (chybějící kontejner = 0).</summary>
    public static async Task<int> BlobCountAsync(this AppFactory factory, string container)
    {
        var client = factory.Services.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(container);
        var count = 0;
        try
        {
            await foreach (var _ in client.GetBlobsAsync(cancellationToken: Ct))
                count++;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
        }

        return count;
    }

    public static byte[] GpxFixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gpx", name));
}
