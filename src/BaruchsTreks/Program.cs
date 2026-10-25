using System.Text.Encodings.Web;
using System.Text.Unicode;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using BaruchsTreks.Cli;
using BaruchsTreks.Http;
using BaruchsTreks.Photos;
using BaruchsTreks.Strava;
using BaruchsTreks.Storage;
using BaruchsTreks.Tracks;
using BaruchsTreks.Trips;
using BaruchsTreks.Users;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.WebEncoders;

// Přepínače příkazové řádky se zpracují před buildem hostu.
if (args.Contains("--healthcheck"))
    return HealthCheckCommand.Run(args);

// Ověří nativní závislosti v image (Magick.NET, HEIC, česká kultura, ICU); bez storage a sítě.
if (args.Contains("--selftest"))
    return SelfTestCommand.Run(Console.Out);

// Jen čtení: žádný StorageBootstrapper ani seeding adminů, takže je bezpečné pustit proti produkčnímu storage.
// Konfigurace se skládá stejně jako v aplikaci (appsettings, proměnné prostředí, argumenty), ale bez buildu hostu.
if (args.Contains("--check-data"))
{
    var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
    var checkConfig = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile($"appsettings.{environment}.json", optional: true)
        .AddEnvironmentVariables()
        .AddCommandLine(args)
        .Build();
    return await CheckDataCommand.RunAsync(checkConfig, Console.Out, CancellationToken.None);
}

var builder = WebApplication.CreateBuilder(args);

// Limit těla požadavku 50 MB (fotky z mobilu).
// Kestrel ho vynucuje sám; UseRequestBodyLimit ho hlídá navíc (TestServer limity nevynucuje a čtení formuláře
// by z výjimky Kestrelu udělalo 400/500 místo 413).
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = RequestBodyLimit.MaxBytes);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = RequestBodyLimit.MaxBytes);

builder.Services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.SectionName);
builder.Services.AddSingleton(sp => new TableServiceClient(StorageConnectionString(sp)));
builder.Services.AddSingleton(sp => new BlobServiceClient(StorageConnectionString(sp)));
builder.Services.AddSingleton<StorageBootstrapper>();
builder.Services.AddSingleton<ITripRepository, TripRepository>();
builder.Services.AddSingleton<IConfigRepository, ConfigRepository>();
builder.Services.AddSingleton<ITrackStore, TrackStore>();
builder.Services.AddSingleton<IPhotoStore, PhotoStore>();
// Strava je volitelná (bez Strava:ClientId a Strava:ClientSecret je import vypnutý). Síť jde jen přes IStravaClient;
// testy ho nahrazují falešným. Typovaný HttpClient je transientní, proto je služba scoped (rotace handlerů).
builder.Services.AddOptions<StravaOptions>().BindConfiguration(StravaOptions.SectionName);
builder.Services.AddStravaClient();
builder.Services.AddSingleton<StravaTokenStore>();
builder.Services.AddScoped<StravaService>();
builder.Services.AddSingleton<StravaOAuthState>();
// Háčky a čárky zůstanou v HTML jako UTF-8 (Django je také nekóduje); <, >, &, ' a " se kódují dál.
builder.Services.Configure<WebEncoderOptions>(options => options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));
builder.Services.AddRazorPages(options =>
{
    // Zakládání výletu sdílí stránku s úpravou (id chybí).
    options.Conventions.AddPageRoute("/TripEdit", "/trips/trip/new");
});
builder.Services.AddAppAuth(builder.Configuration);

var app = builder.Build();

// Jediné místo, které vytváří tabulky a kontejnery; před prvním požadavkem.
await app.Services.GetRequiredService<StorageBootstrapper>().EnsureAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<AdminSeeder>().SeedAsync(app.Lifetime.ApplicationStopping);

// Musí být první, aby ostatní middleware viděl skutečné schéma a IP klienta za Caddy.
// Hlavičky se zpracují jen s ASPNETCORE_FORWARDEDHEADERS_ENABLED=true (nastavuje compose).
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    // Žádné podrobnosti o výjimce ven: Pages/Error.cshtml ukáže jen obecnou hlášku (podrobnosti jsou v logu).
    app.UseExceptionHandler("/error");
}

// 404 a ostatní chybové stavy bez těla dostanou stránku z Pages/Error.cshtml se zachovaným stavovým kódem.
app.UseStatusCodePagesWithReExecute("/error");
app.UseStaticFiles();
app.UseRequestBodyLimit();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Json(new { status = "ok" }));
app.MapGet("/", () => Results.Redirect("/trips/"));
app.MapTrackEndpoints();

app.MapRazorPages();

app.Run();
return 0;

static string StorageConnectionString(IServiceProvider sp) =>
    sp.GetRequiredService<IConfiguration>().GetConnectionString("Storage")
    ?? throw new InvalidOperationException("Missing configuration value ConnectionStrings:Storage.");

public partial class Program;
