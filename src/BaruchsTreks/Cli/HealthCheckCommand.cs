namespace BaruchsTreks.Cli;

/// <summary>Režim <c>--healthcheck</c> pro Docker HEALTHCHECK: exit code 0 = HTTP 200, jinak 1.</summary>
public static class HealthCheckCommand
{
    public const string DefaultUrl = "http://localhost:8080/healthz";

    /// <summary>Volitelný argument začínající <c>http</c> přepíše adresu (pro testy).</summary>
    public static int Run(string[] args)
    {
        var url = args.FirstOrDefault(a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ?? DefaultUrl;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = http.GetAsync(url).GetAwaiter().GetResult();
            return response.StatusCode == System.Net.HttpStatusCode.OK ? 0 : 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}
