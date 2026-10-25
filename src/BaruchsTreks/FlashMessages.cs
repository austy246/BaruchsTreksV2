using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace BaruchsTreks;

/// <summary>
/// Zprávy pro další stránku (náhrada <c>django.contrib.messages</c>). Ukládají se do TempData jako pole řetězců
/// <c>úroveň|text</c> a layout je zobrazí a zahodí.
/// </summary>
public static class FlashMessages
{
    private const string Key = "flash-messages";

    /// <summary><paramref name="level"/> je <c>success</c>, <c>error</c>, <c>warning</c> nebo <c>info</c> (Django <c>message.tags</c>).</summary>
    public static void AddMessage(this ITempDataDictionary tempData, string level, string text)
    {
        var existing = tempData.Peek(Key) as string[] ?? [];
        tempData[Key] = existing.Append($"{level}|{text}").ToArray();
    }

    /// <summary>Vrátí a odstraní čekající zprávy jako dvojice (tags, text).</summary>
    public static IReadOnlyList<(string Tags, string Text)> TakeMessages(this ITempDataDictionary tempData)
    {
        if (tempData[Key] is not string[] raw)
            return [];
        return raw
            .Select(item => item.Split('|', 2))
            .Select(parts => parts.Length == 2 ? (parts[0], parts[1]) : ("", parts[0]))
            .ToList();
    }
}
