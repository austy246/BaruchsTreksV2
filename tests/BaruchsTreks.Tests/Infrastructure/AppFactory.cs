using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BaruchsTreks.Tests;

/// <summary>
/// Spustí aplikaci proti Azurite s unikátními jmény tabulek a kontejnerů,
/// takže testy se navzájem neovlivňují.
/// </summary>
public sealed partial class AppFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string> _settings;
    private readonly List<Action<IServiceCollection>> _serviceOverrides = [];

    public string TripsTable { get; }
    public string UsersTable { get; }
    public string PhotosContainer { get; }
    public string TracksContainer { get; }
    public string KeysContainer { get; }

    public AppFactory(AzuriteFixture azurite) : this(azurite, null)
    {
    }

    /// <summary>Druhá instance aplikace nad stejnými tabulkami a kontejnery (např. simulace restartu).</summary>
    public AppFactory(AzuriteFixture azurite, AppFactory? sharedStorageWith)
    {
        TripsTable = sharedStorageWith?.TripsTable ?? "Trips" + Hex();
        UsersTable = sharedStorageWith?.UsersTable ?? "Users" + Hex();
        PhotosContainer = sharedStorageWith?.PhotosContainer ?? "photos-" + Hex();
        TracksContainer = sharedStorageWith?.TracksContainer ?? "tracks-" + Hex();
        KeysContainer = sharedStorageWith?.KeysContainer ?? "keys-" + Hex();

        _settings = new Dictionary<string, string>
        {
            ["ConnectionStrings:Storage"] = azurite.ConnectionString,
            ["Storage:TripsTable"] = TripsTable,
            ["Storage:UsersTable"] = UsersTable,
            ["Storage:PhotosContainer"] = PhotosContainer,
            ["Storage:TracksContainer"] = TracksContainer,
            ["Storage:KeysContainer"] = KeysContainer,
            ["Storage:PublicBlobBaseUrl"] = "http://blob.test/",
            ["Mapy:ApiKey"] = "test-key",
        };
    }

    /// <summary>Vymění služby v kontejneru (např. IStravaClient za fake). Volat před prvním CreateClient.</summary>
    public AppFactory ConfigureTestServices(Action<IServiceCollection> configure)
    {
        _serviceOverrides.Add(configure);
        return this;
    }

    /// <summary>Doplní nebo přepíše konfigurační hodnotu. Volat před prvním CreateClient.</summary>
    public AppFactory WithSetting(string key, string value)
    {
        _settings[key] = value;
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Dřív než se cokoli spustí: testy nesmí nikdy sáhnout na jiné storage než lokální Azurite.
        AssertLocalStorage(_settings.GetValueOrDefault("ConnectionStrings:Storage"));
        AssertLocalStorage(Environment.GetEnvironmentVariable("ConnectionStrings__Storage"));

        builder.UseEnvironment("Development");

        // UseSetting je vidět už při buildu hostu, tedy i pro hodnoty čtené přímo v Program.cs.
        foreach (var (key, value) in _settings)
            builder.UseSetting(key, value);

        builder.ConfigureTestServices(services =>
        {
            foreach (var configure in _serviceOverrides)
                configure(services);
        });
    }

    private static string Hex() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Selže, pokud connection string nemíří výhradně na 127.0.0.1/localhost (Azurite). Prázdná hodnota projde
    /// (proměnná prostředí nemusí být nastavená); chybějící endpoint v neprázdném řetězci ne.
    /// </summary>
    internal static void AssertLocalStorage(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var hosts = EndpointPattern().Matches(connectionString).Select(m => m.Groups[1].Value).ToList();
        if (hosts.Count == 0 || hosts.Any(h => h is not ("127.0.0.1" or "localhost")))
        {
            throw new InvalidOperationException(
                "Tests must run against local Azurite only: ConnectionStrings:Storage has a non-local or missing endpoint.");
        }
    }

    [GeneratedRegex(@"https?://([^:/;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EndpointPattern();
}
