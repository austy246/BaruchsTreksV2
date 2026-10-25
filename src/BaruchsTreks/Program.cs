using Azure.Data.Tables;
using Azure.Storage.Blobs;
using BaruchsTreks.Cli;
using BaruchsTreks.Storage;
using BaruchsTreks.Trips;
using Microsoft.AspNetCore.Http.Features;

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
const long MaxRequestBodyBytes = 52_428_800;
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaxRequestBodyBytes);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = MaxRequestBodyBytes);

builder.Services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.SectionName);
builder.Services.AddSingleton(sp => new TableServiceClient(StorageConnectionString(sp)));
builder.Services.AddSingleton(sp => new BlobServiceClient(StorageConnectionString(sp)));
builder.Services.AddSingleton<StorageBootstrapper>();
builder.Services.AddSingleton<ITripRepository, TripRepository>();
builder.Services.AddSingleton<IConfigRepository, ConfigRepository>();

var app = builder.Build();

// Jediné místo, které vytváří tabulky a kontejnery; před prvním požadavkem.
await app.Services.GetRequiredService<StorageBootstrapper>().EnsureAsync(app.Lifetime.ApplicationStopping);

// Musí být první, aby ostatní middleware viděl skutečné schéma a IP klienta za Caddy.
// Hlavičky se zpracují jen s ASPNETCORE_FORWARDEDHEADERS_ENABLED=true (nastavuje compose).
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    // Žádné podrobnosti o výjimce ven.
    app.UseExceptionHandler(errorApp => errorApp.Run(context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return context.Response.WriteAsync("Internal Server Error");
    }));
    app.UseStatusCodePages();
}

app.MapGet("/healthz", () => Results.Json(new { status = "ok" }));

app.Run();
return 0;

static string StorageConnectionString(IServiceProvider sp) =>
    sp.GetRequiredService<IConfiguration>().GetConnectionString("Storage")
    ?? throw new InvalidOperationException("Missing configuration value ConnectionStrings:Storage.");

public partial class Program;
