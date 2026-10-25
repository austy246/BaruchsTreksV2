using Microsoft.AspNetCore.Http.Features;
using BaruchsTreks.Cli;

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

var app = builder.Build();

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

public partial class Program;
