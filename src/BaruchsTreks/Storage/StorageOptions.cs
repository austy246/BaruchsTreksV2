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

    /// <summary>
    /// Veřejná základní URL ÚČTU blobů (ne kontejneru); vždy končí lomítkem, nebo je prázdná. Adresa fotky je
    /// <c>{PublicBlobBaseUrl}{PhotosContainer}/{jméno}</c>, např. <c>https://baruchstreks.blob.core.windows.net/</c>
    /// nebo lokálně <c>http://localhost:10200/devstoreaccount1/</c>. Prázdná = adresa kontejneru, na kterou se připojuje aplikace.
    /// </summary>
    public string PublicBlobBaseUrl
    {
        get => _publicBlobBaseUrl;
        set => _publicBlobBaseUrl = string.IsNullOrWhiteSpace(value) ? "" : value.Trim().TrimEnd('/') + "/";
    }
}
