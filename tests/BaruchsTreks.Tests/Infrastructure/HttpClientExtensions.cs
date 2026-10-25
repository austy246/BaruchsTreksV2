using System.Text.RegularExpressions;

namespace BaruchsTreks.Tests;

public static partial class HttpClientExtensions
{
    /// <summary>
    /// Načte stránku s formulářem a vrátí hodnotu skrytého pole <c>__RequestVerificationToken</c>
    /// (cookie s antiforgery tokenem si klient uloží sám).
    /// </summary>
    public static async Task<string> GetAntiforgeryTokenAsync(this HttpClient client, string url, CancellationToken ct)
    {
        var html = await client.GetStringAsync(url, ct);
        var match = TokenPattern().Match(html);
        Assert.True(match.Success, $"No antiforgery token found on {url}.");
        return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
