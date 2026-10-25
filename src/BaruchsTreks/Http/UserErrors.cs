namespace BaruchsTreks.Http;

/// <summary>Texty chyb pro uživatele: podrobnosti výjimky jen ve vývoji, jinak jen obecná věta (podrobnosti jsou v logu).</summary>
public static class UserErrors
{
    public static string Message(IHostEnvironment environment, string generic, Exception exception) =>
        environment.IsDevelopment() ? $"{generic}: {exception.Message}" : $"{generic}.";
}
