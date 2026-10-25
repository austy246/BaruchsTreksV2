namespace BaruchsTreks.Storage;

/// <summary>Sekce <c>Storage</c> v konfiguraci. Jména se přepisují jen kvůli izolaci testů.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    private string _publicBlobBaseUrl = "";

    public string TripsTable { get; set; } = "Trips";
    public string UsersTable { get; set; } = "BaruchsTreksUsers";
    public string PhotosContainer { get; set; } = "photos";
    public string TracksContainer { get; set; } = "tracks";
    public string KeysContainer { get; set; } = "baruchstreks-keys";

    /// <summary>Veřejná základní URL blobů; vždy končí lomítkem (nebo je prázdná).</summary>
    public string PublicBlobBaseUrl
    {
        get => _publicBlobBaseUrl;
        set => _publicBlobBaseUrl = string.IsNullOrWhiteSpace(value) ? "" : value.Trim().TrimEnd('/') + "/";
    }
}
