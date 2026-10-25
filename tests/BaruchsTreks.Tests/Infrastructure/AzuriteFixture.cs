using Testcontainers.Azurite;

[assembly: AssemblyFixture(typeof(BaruchsTreks.Tests.AzuriteFixture))]

namespace BaruchsTreks.Tests;

/// <summary>Jeden Azurite kontejner sdílený všemi testy v assembly.</summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    public const string Image = "mcr.microsoft.com/azure-storage/azurite:3.35.0";

    private readonly AzuriteContainer _container = new AzuriteBuilder(Image)
        .WithCommand("--skipApiVersionCheck", "--loose")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
