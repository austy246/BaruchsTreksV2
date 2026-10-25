using System.Globalization;
using System.Text;

namespace BaruchsTreks;

/// <summary>
/// Výstup stejný jako Django filtry a vykreslení hodnot v šablonách. Vždy invariantní kultura: server může běžet
/// s <c>cs-CZ</c> a čísla v HTML, JSON i odkazech musí mít desetinnou tečku.
/// </summary>
public static class DjangoFormat
{
    /// <summary>
    /// Python <c>float</c> tak, jak ho vypíše Django šablona (<c>4.5</c>, <c>4.0</c>, nikdy <c>4,5</c>).
    /// </summary>
    public static string Float(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.IndexOfAny(['.', 'E', 'N', 'I']) >= 0 ? text : text + ".0";
    }

    /// <summary>
    /// Celé číslo (Python <c>int</c>: metry, nadmořská výška) bez desetinné části; neceločíselná hodnota zůstane float.
    /// </summary>
    public static string Number(double value) =>
        value == Math.Truncate(value) && Math.Abs(value) < 1e15
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : Float(value);

    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Filtr <c>truncatewords</c>: prvních <paramref name="count"/> slov spojených jednou mezerou a " …",
    /// když text slov obsahuje víc. Text se pak vypisuje s HTML escapováním jako každá proměnná.
    /// </summary>
    public static string TruncateWords(string? text, int count)
    {
        var words = (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length > count ? string.Join(' ', words.Take(count)) + " …" : string.Join(' ', words);
    }

    /// <summary>
    /// Filtr <c>escapejs</c>: bezpečné vložení do řetězce v JavaScriptu (<c>\uXXXX</c> pro uvozovky, lomítka, značky a
    /// řídicí znaky). Výstup obsahuje jen znaky bezpečné v <c>&lt;script&gt;</c>, proto se vypisuje bez HTML kódování.
    /// </summary>
    public static string EscapeJs(string? value)
    {
        var sb = new StringBuilder();
        foreach (var c in value ?? "")
        {
            if (c is '\\' or '\'' or '"' or '>' or '<' or '&' or '=' or '-' or ';' or '`' or '\u2028' or '\u2029' or < ' ')
                sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            else
                sb.Append(c);
        }

        return sb.ToString();
    }
}
