using System.Globalization;
using Azure.Data.Tables;

namespace BaruchsTreks.Storage;

/// <summary>Tolerantní čtení hodnot z <see cref="TableEntity"/> (produkční data mají smíšené typy).</summary>
public static class TableValue
{
    /// <summary>Řetězec; chybějící/null je prázdný, čísla a data se formátují invariantně.</summary>
    public static string String(TableEntity e, string key, string fallback = "") =>
        !e.TryGetValue(key, out var v) || v is null
            ? fallback
            : v switch
            {
                string s => s,
                DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => v.ToString() ?? fallback,
            };

    /// <summary>Číslo z Int32, Int64, Double i číselného řetězce; cokoli jiného (včetně "" a NaN) je null.</summary>
    public static double? Number(TableEntity e, string key)
    {
        if (!e.TryGetValue(key, out var v))
            return null;
        double? n = v switch
        {
            int i => i,
            long l => l,
            double d => d,
            float f => f,
            decimal m => (double)m,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
        return n is { } x && double.IsFinite(x) ? x : null;
    }

    public static double Number(TableEntity e, string key, double fallback) => Number(e, key) ?? fallback;

    /// <summary>Datum a čas z datetime vlastnosti nebo ISO řetězce (bez zóny = UTC); jinak null.</summary>
    public static DateTimeOffset? Date(object? value) => value switch
    {
        DateTimeOffset d => d,
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, dt.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : dt.Kind)),
        string s when !string.IsNullOrWhiteSpace(s)
            && DateTimeOffset.TryParse(s.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var parsed) => parsed,
        _ => null,
    };
}
