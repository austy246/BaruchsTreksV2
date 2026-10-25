using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using BaruchsTreks.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BaruchsTreks.Tests;

/// <summary>
/// Spustí aplikaci proti Azurite s unikátními jmény tabulek a kontejnerů,
/// takže testy se navzájem neovlivňují.
/// </summary>
public sealed partial class AppFactory : WebApplicationFactory<Program>
{
    public const string TestPassword = "test-password";

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

    /// <summary>Založí uživatele přímo v tabulce (bez přihlášení).</summary>
    public async Task CreateUserAsync(string userName, string password, bool isAdmin)
    {
        var hasher = Services.GetRequiredService<IPasswordHasher<AppUser>>();
        var placeholder = new AppUser(userName, "", isAdmin, "2020-01-01T00:00:00.000000");
        await Services.GetRequiredService<IUserRepository>().UpsertAsync(
            placeholder with { PasswordHash = hasher.HashPassword(placeholder, password) });
    }

    /// <summary>Přihlásí klienta přes formulář /accounts/login/. Vrátí odpověď (302, nebo 200 při neúspěchu).</summary>
    public async Task<HttpResponseMessage> LoginAsync(HttpClient client, string userName, string password, string? next = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var url = next is null ? "/accounts/login/" : "/accounts/login/?next=" + Uri.EscapeDataString(next);
        var token = await client.GetAntiforgeryTokenAsync(url, ct);
        return await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = userName,
            ["password"] = password,
            ["__RequestVerificationToken"] = token,
        }), ct);
    }

    /// <summary>Klient přihlášený jako nový uživatel. Nesleduje přesměrování, drží cookies.</summary>
    public async Task<HttpClient> CreateUserClientAsync(bool isAdmin)
    {
        var userName = (isAdmin ? "admin-" : "user-") + Hex();
        await CreateUserAsync(userName, TestPassword, isAdmin);

        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await LoginAsync(client, userName, TestPassword);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Dřív než se cokoli spustí: testy nesmí nikdy sáhnout na jiné storage než lokální Azurite.
        AssertLocalStorage(_settings.GetValueOrDefault("ConnectionStrings:Storage"));
        AssertLocalStorage(Environment.GetEnvironmentVariable("ConnectionStrings__Storage"));

        builder.UseEnvironment("Development");

        // Klíče jsou záměrně nešifrované (viz AuthSetup); varování XmlKeyManager[35] by jen zašumělo výstup testů.
        builder.ConfigureLogging(l => l.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error));

        // UseSetting je vidět už při buildu hostu, tedy i pro hodnoty čtené přímo v Program.cs.
        foreach (var (key, value) in _settings)
            builder.UseSetting(key, value);

        builder.ConfigureTestServices(services =>
        {
            services.AddTransient<IStartupFilter, TestEndpointsStartupFilter>();
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

    /// <summary>
    /// Testovací endpointy: <c>/__whoami</c> (200 se jménem přihlášeného, jinak 401; ověřuje cookie standardním schématem,
    /// takže odhalí i cookie, kterou aplikace nedokáže rozšifrovat) a <c>/__admin</c> (politika AdminOnly).
    /// </summary>
    private sealed class TestEndpointsStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, pipeline) =>
            {
                if (context.Request.Path != "/__whoami")
                {
                    await pipeline();
                    return;
                }

                var result = await context.AuthenticateAsync();
                if (result.Succeeded)
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    await context.Response.WriteAsync(result.Principal.FindFirstValue(ClaimTypes.Name) ?? "");
                }
                else
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                }
            });
            next(app);
            app.UseEndpoints(endpoints => endpoints
                .MapGet("/__admin", () => "admin-ok")
                .RequireAuthorization(AuthSetup.AdminPolicy));
        };
    }
}
