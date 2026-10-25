using System.Globalization;
using System.Text;
using BaruchsTreks.Photos;
using ImageMagick;

namespace BaruchsTreks.Cli;

/// <summary>
/// Režim <c>--selftest</c>: ověří, že runtime image umí to, co aplikace potřebuje (nativní Magick.NET s HEIC,
/// převod vzorku HEIC na JPEG, česká kultura a ICU s diakritikou). Nesahá do storage ani do sítě.
/// Výstup má jeden řádek na kontrolu (<c>OK název</c> / <c>FAIL název: důvod</c>); návratový kód 0 = vše prošlo.
/// </summary>
public static class SelfTestCommand
{
    /// <summary>Vzorek HEIC se publikuje jako obsah vedle aplikace (viz csproj).</summary>
    public static string SampleHeicPath => Path.Combine(AppContext.BaseDirectory, "SelfTest", "sample.heic");

    private const string CzechLetters = "ěščřžýáíé";

    public static int Run(TextWriter output)
    {
        var allPassed = true;
        allPassed &= Check(output, "Magick.NET native library loads and reads HEIC", CheckMagickLoads);
        allPassed &= Check(output, "sample.heic converts to JPEG", CheckHeicConversion);
        allPassed &= Check(output, "cs-CZ culture can be created", CheckCzechCulture);
        allPassed &= Check(output, "ICU normalization folds diacritics", CheckDiacriticsFold);
        allPassed &= Check(output, "ICU cs-CZ collation orders ch after h", CheckCzechCollation);
        return allPassed ? 0 : 1;
    }

    private static bool Check(TextWriter output, string name, Func<string?> check)
    {
        try
        {
            var problem = check();
            output.WriteLine(problem is null ? $"OK {name}" : $"FAIL {name}: {problem}");
            return problem is null;
        }
        catch (Exception ex)
        {
            output.WriteLine($"FAIL {name}: {ex.GetType().Name}: {OneLine(ex.Message)}");
            return false;
        }
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ");

    private static string? CheckMagickLoads()
    {
        // Samotný přístup k MagickNET zavede nativní knihovnu; selhání vyhodí výjimku, kterou Check zachytí.
        var heic = MagickNET.SupportedFormats.FirstOrDefault(f => f.Format == MagickFormat.Heic);
        if (heic is null)
            return "HEIC is not in MagickNET.SupportedFormats";
        return heic.SupportsReading ? null : "HEIC is listed but cannot be read";
    }

    private static string? CheckHeicConversion()
    {
        var path = SampleHeicPath;
        if (!File.Exists(path))
            return $"fixture not found: {path}";

        using var input = File.OpenRead(path);
        var photo = PhotoProcessor.Prepare(input, "sample.heic");
        using var content = photo.Content;
        if (photo.Extension != ".jpg" || photo.ContentType != "image/jpeg")
            return $"expected .jpg image/jpeg, got {photo.Extension} {photo.ContentType}";

        content.Position = 0;
        var info = new MagickImageInfo(content);
        return info.Format == MagickFormat.Jpeg && info.Width > 0 && info.Height > 0
            ? null
            : $"result is {info.Format} {info.Width}x{info.Height}, expected a non-empty JPEG";
    }

    private static string? CheckCzechCulture()
    {
        // Bez ICU (invariantní režim) vznikne neznámá kultura nebo výjimka CultureNotFoundException.
        var culture = new CultureInfo("cs-CZ", useUserOverride: false);
        if (culture.Name != "cs-CZ")
            return $"got culture '{culture.Name}'";
        var separator = culture.NumberFormat.NumberDecimalSeparator;
        return separator == "," ? null : $"decimal separator is '{separator}', expected ','";
    }

    private static string? CheckDiacriticsFold()
    {
        var decomposed = CzechLetters.Normalize(NormalizationForm.FormD);
        var folded = new string(decomposed
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        if (folded != "escrzyaie")
            return $"expected 'escrzyaie', got '{folded}'";

        // Kořenová (invariantní) kolace: v české jsou č, š, ž, ř samostatná písmena, takže tam by IgnoreNonSpace rozdílný zůstal.
        return CultureInfo.InvariantCulture.CompareInfo.Compare(CzechLetters, "escrzyaie", CompareOptions.IgnoreNonSpace) == 0
            ? null
            : "CompareOptions.IgnoreNonSpace does not equate the Czech letters with their base letters";
    }

    private static string? CheckCzechCollation()
    {
        // Česká abeceda: h < ch < i a c < č < d. Ordinální řazení by dalo opak (c < h).
        var compare = new CultureInfo("cs-CZ", useUserOverride: false).CompareInfo;
        if (compare.Compare("hrad", "chrám") >= 0)
            return "expected 'hrad' < 'chrám'";
        if (compare.Compare("chrám", "iglú") >= 0)
            return "expected 'chrám' < 'iglú'";
        return compare.Compare("čaj", "dub") < 0 && compare.Compare("cukr", "čaj") < 0
            ? null
            : "expected 'cukr' < 'čaj' < 'dub'";
    }
}
