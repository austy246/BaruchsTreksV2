using BaruchsTreks.Tracks;

namespace BaruchsTreks.Http;

/// <summary>Texty chyb pro uživatele: podrobnosti výjimky jen ve vývoji, jinak jen obecná věta (podrobnosti jsou v logu).</summary>
public static class UserErrors
{
    public static string Message(IHostEnvironment environment, string generic, Exception exception) =>
        environment.IsDevelopment() ? $"{generic}: {exception.Message}" : $"{generic}.";

    /// <summary>Hlášky parseru jsou určené uživateli; podrobnosti z XML parseru (řádek, pozice) jen ve vývoji.</summary>
    public static string GpxMessage(IHostEnvironment environment, GpxParseError error) =>
        error.InnerException is null || environment.IsDevelopment()
            ? error.Message
            : "Soubor se nepodařilo přečíst jako GPX.";
}
