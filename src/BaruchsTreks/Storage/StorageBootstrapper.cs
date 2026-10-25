using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BaruchsTreks.Storage;

/// <summary>
/// Jediné místo, které smí vytvářet tabulky a kontejnery. Idempotentní; přístup existujícího
/// kontejneru nikdy nemění. Veřejné čtení <c>photos</c> jen v Development a jen u nově vytvořeného.
/// </summary>
public sealed class StorageBootstrapper(
    TableServiceClient tables,
    BlobServiceClient blobs,
    IOptions<StorageOptions> options,
    IHostEnvironment environment)
{
    private readonly StorageOptions _options = options.Value;

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        await tables.CreateTableIfNotExistsAsync(_options.TripsTable, cancellationToken);
        await tables.CreateTableIfNotExistsAsync(_options.UsersTable, cancellationToken);

        // CreateIfNotExists u existujícího kontejneru nic nemění, takže přístup platí jen pro nově vytvořený.
        var photosAccess = environment.IsDevelopment() ? PublicAccessType.Blob : PublicAccessType.None;
        await Container(_options.PhotosContainer).CreateIfNotExistsAsync(photosAccess, cancellationToken: cancellationToken);
        await Container(_options.TracksContainer).CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
        await Container(_options.KeysContainer).CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
    }

    private BlobContainerClient Container(string name) => blobs.GetBlobContainerClient(name);
}
