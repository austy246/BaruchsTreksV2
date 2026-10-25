using System.Text.RegularExpressions;

namespace BaruchsTreks.Tests;

/// <summary>Soubor v multipart formuláři.</summary>
public sealed record UploadFile(string Field, string FileName, byte[] Content, string ContentType = "application/octet-stream");

public static partial class HttpClientExtensions
{
    /// <summary>
    /// Odešle formulář jako multipart. Antiforgery token se vezme ze stránky <paramref name="tokenUrl"/>
    /// (cookie si klient uloží sám). Stejné jméno pole lze uvést víckrát.
    /// </summary>
    public static async Task<HttpResponseMessage> PostFormAsync(
        this HttpClient client,
        string url,
        string tokenUrl,
        IEnumerable<(string Name, string Value)> fields,
        params UploadFile[] files)
    {
        var ct = TestContext.Current.CancellationToken;
        var token = await client.GetAntiforgeryTokenAsync(tokenUrl, ct);
        using var form = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };
        foreach (var (name, value) in fields)
            form.Add(new StringContent(value), name);
        foreach (var file in files)
        {
            var part = new ByteArrayContent(file.Content);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType);
            form.Add(part, file.Field, file.FileName);
        }

        return await client.PostAsync(url, form, ct);
    }

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
