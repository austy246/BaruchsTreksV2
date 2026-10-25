using System.Text.Encodings.Web;
using System.Text.Unicode;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using BaruchsTreks.Cli;
using BaruchsTreks.Http;
using BaruchsTreks.Photos;
using BaruchsTreks.Storage;
using BaruchsTreks.Tracks;
using BaruchsTreks.Trips;
using BaruchsTreks.Users;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.WebEncoders;

// Přepínače příkazové řádky se zpracují před buildem hostu.
if (args.Contains("--healthcheck"))
    return HealthCheckCommand.Run(args);

// Doplní se v dalších úkolech (kontrola dat ve storage, test nativních závislostí v image).
if (args.Contains("--check-data") || args.Contains("--selftest"))
{
    Console.Error.WriteLine("Not implemented yet: --check-data and --selftest.");
    return 2;
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
