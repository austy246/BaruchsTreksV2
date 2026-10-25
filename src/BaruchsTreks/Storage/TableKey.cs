namespace BaruchsTreks.Storage;

public static class TableKey
{
    /// <summary>
    /// Klíč entity nesmí být prázdný ani obsahovat / \ # ? a řídicí znaky. Azurite na takové klíče
    /// odpovídá chybou, proto je odmítáme dřív, než se dotaz vůbec odešle.
    /// </summary>
    public static bool IsValid(string? key) =>
        !string.IsNullOrEmpty(key)
        && key.All(c => c is not ('/' or '\\' or '#' or '?') && !char.IsControl(c));
}
