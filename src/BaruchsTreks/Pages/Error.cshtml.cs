using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BaruchsTreks.Pages;

/// <summary>
/// Chybová stránka pro neošetřené výjimky (<c>UseExceptionHandler</c>) a stavy bez těla jako 404
/// (<c>UseStatusCodePagesWithReExecute</c>). Nikdy neukazuje výjimku ani stack trace; podrobnosti jsou jen v logu.
/// </summary>
[IgnoreAntiforgeryToken]
public class ErrorModel(TimeProvider clock) : PageModel
{
    public int Status { get; private set; } = StatusCodes.Status500InternalServerError;

    /// <summary>Cesta původního požadavku (bez dotazu), jako <c>request.path</c> v Django.</summary>
    public string OriginalPath { get; private set; } = "";

    public string Time => clock.GetUtcNow().ToString("yyyy-MM-dd HH:mm:ss");

    // Stránka musí odpovědět na každou metodu původního požadavku (výjimka mohla vzniknout při POST).
    public void OnGet() => Init();
    public void OnPost() => Init();
    public void OnPut() => Init();
    public void OnDelete() => Init();
    public void OnPatch() => Init();

    private void Init()
    {
        var exception = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
        var reExecute = HttpContext.Features.Get<IStatusCodeReExecuteFeature>();
        OriginalPath = exception?.Path ?? reExecute?.OriginalPath ?? Request.Path.Value ?? "";

        // Při výjimce nastavil middleware 500, u stavových stránek zůstává původní kód.
        if (exception is not null && Response.StatusCode < 400)
            Response.StatusCode = StatusCodes.Status500InternalServerError;
        Status = Response.StatusCode;
    }
}
