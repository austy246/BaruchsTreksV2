using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using BaruchsTreks.Storage;
using BaruchsTreks.Trips;

namespace BaruchsTreks.Cli;

/// <summary>
/// Režim <c>--check-data</c>: jen čte tabulku výletů a kontejnery fotek a tras a hlásí, co v nich je (převod každého
/// výletu, parsovatelnost config řádků, HEIC fotky, osiřelé a chybějící trasy, typy čísel). Nic nezapisuje ani
/// nezakládá (žádný <see cref="StorageBootstrapper"/>), takže je bezpečný i proti produkčnímu storage.
/// Nevypisuje hodnoty konfigurace ani tokenů a nikdy connection string.
/// Návratový kód: 0 = všechny entity čitelné, 1 = nečitelný výlet, nečitelný config řádek, chybějící tabulka výletů,
/// chybějící konfigurace nebo nedostupný storage. HEIC fotky, osiřelé trasy a chybějící kontejnery jsou jen upozornění.
/// </summary>
public static class CheckDataCommand
{
    private const string TripsFilter = "PartitionKey eq 'Trips'";
    private const string ConfigFilter = "PartitionKey eq 'Config'";
    private const int MaxListed = 50;

    /// <summary>Pole, jejichž typ v tabulce rozhoduje o formátování v UI (Int32/Int64 vs. Double).</summary>
    private static readonly string[] NumericFields = ["LengthHours", "ElevationGain", "MetersAscend", "MetersDescend"];

    public static Task<int> RunAsync(IConfiguration config, TextWriter? output = null, CancellationToken cancellationToken = default) =>
        RunAsync(config, output ?? Console.Out, TripEntityMapper.FromEntity, cancellationToken);

    /// <summary>Varianta s vlastním převodníkem entity (kvůli testu větve s nečitelnými výlety).</summary>
    public static async Task<int> RunAsync(
        IConfiguration config, TextWriter output, Func<TableEntity, Trip> read, CancellationToken cancellationToken)
    {
        var connectionString = config.GetConnectionString("Storage");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            await output.WriteLineAsync("Missing configuration value ConnectionStrings:Storage.");
            return 1;
        }

        var options = config.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();

        TableServiceClient tables;
        BlobServiceClient blobs;
        try
        {
            // Kratší opakování než ve službě: nedostupný storage se má v CLI ohlásit hned, ne po desítkách vteřin.
            var tableOptions = new TableClientOptions { Retry = { MaxRetries = 2, Delay = TimeSpan.FromMilliseconds(500) } };
            var blobOptions = new BlobClientOptions { Retry = { MaxRetries = 2, Delay = TimeSpan.FromMilliseconds(500) } };
            tables = new TableServiceClient(connectionString, tableOptions);
            blobs = new BlobServiceClient(connectionString, blobOptions);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            await output.WriteLineAsync("ConnectionStrings:Storage is not a valid storage connection string.");
            return 1;
        }

        try
        {
            return await CheckAsync(tables, blobs, options, output, read, cancellationToken);
        }
        catch (TableNotFoundException ex)
        {
            await output.WriteLineAsync($"Table {ex.TableName} not found");
            return 1;
        }
        catch (RequestFailedException ex)
        {
            // Jen status a kód: zpráva SDK může obsahovat adresu a hlavičky požadavku.
            await output.WriteLineAsync($"Storage read failed: HTTP {ex.Status} {ex.ErrorCode}".TrimEnd());
            return 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await output.WriteLineAsync($"Storage read failed: {ex.GetType().Name}");
            return 1;
        }
    }

    private static async Task<int> CheckAsync(
        TableServiceClient tables, BlobServiceClient blobs, StorageOptions options, TextWriter output,
        Func<TableEntity, Trip> read, CancellationToken ct)
    {
        var table = tables.GetTableClient(options.TripsTable);

        // Výlety: každou entitu zkusit převést a spočítat typy čísel.
        var tripKeys = new HashSet<string>(StringComparer.Ordinal);
        var tripsWithTrack = new List<string>();
        var unreadable = new List<string>();
        var census = NumericFields.ToDictionary(f => f, _ => new TypeCensus());
        var tripCount = 0;
        try
        {
            await foreach (var entity in table.QueryAsync<TableEntity>(TripsFilter, cancellationToken: ct))
            {
                tripCount++;
                tripKeys.Add(entity.RowKey);
                foreach (var field in NumericFields)
                    census[field].Add(entity, field);
                try
                {
                    var trip = read(entity);
                    if (trip.TrackJson.Length > 0)
                        tripsWithTrack.Add(entity.RowKey);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    unreadable.Add($"{entity.RowKey}: {ex.Message}");
                }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404 && ex.ErrorCode == "TableNotFound")
        {
            throw new TableNotFoundException(options.TripsTable);
        }

        // Config řádky: jen počet a parsovatelnost JSON; hodnota (tokeny) se nikdy nevypisuje.
        var configCount = 0;
        var emptyConfig = 0;
        var unparsableConfig = new List<string>();
        await foreach (var entity in table.QueryAsync<TableEntity>(ConfigFilter, cancellationToken: ct))
        {
            configCount++;
            var value = TableValue.String(entity, "Value");
            if (value.Length == 0)
            {
                emptyConfig++;
                continue;
            }
            if (!IsJson(value))
                unparsableConfig.Add(entity.RowKey);
        }

        // Fotky a trasy: jen výpis bloků. Chybějící kontejner není chyba (vznikají při prvním zápisu), jen se ohlásí.
        var photoNames = new List<(string Name, string ContentType)>();
        var photosMissing = !await TryListAsync(blobs.GetBlobContainerClient(options.PhotosContainer),
            (name, type) => photoNames.Add((name, type)), ct);
        var trackBlobs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var trackBlobCount = 0;
        var tracksMissing = !await TryListAsync(blobs.GetBlobContainerClient(options.TracksContainer), (name, _) =>
        {
            trackBlobCount++;
            var slash = name.IndexOf('/');
            var tripId = slash < 0 ? "" : name[..slash];
            var file = slash < 0 ? name : name[(slash + 1)..];
            if (!trackBlobs.TryGetValue(tripId, out var files))
                trackBlobs[tripId] = files = new HashSet<string>(StringComparer.Ordinal);
            files.Add(file);
        }, ct);

        var heic = photoNames.Where(p => IsHeic(p.Name, p.ContentType)).Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        var orphans = trackBlobs.Keys.Where(id => !tripKeys.Contains(id)).Order(StringComparer.Ordinal).ToList();
        var incomplete = new List<string>();
        foreach (var id in tripsWithTrack.Order(StringComparer.Ordinal))
        {
            trackBlobs.TryGetValue(id, out var files);
            var missing = new[] { TrackFiles.Gpx, TrackFiles.GeoJson }.Where(f => files is null || !files.Contains(f)).ToList();
            if (missing.Count > 0)
                incomplete.Add($"{id} (missing {string.Join(", ", missing)})");
        }

        await output.WriteLineAsync($"Storage account: {tables.AccountName}");
        await output.WriteLineAsync($"Trips: {tripCount}, unreadable: {unreadable.Count}");
        foreach (var line in unreadable)
            await output.WriteLineAsync($"  {line}");
        foreach (var field in NumericFields)
            await output.WriteLineAsync($"{field}: {census[field]}");

        await output.WriteLineAsync($"Config rows: {configCount}, unparsable: {unparsableConfig.Count}");
        foreach (var key in unparsableConfig)
            await output.WriteLineAsync($"  {key}: Value is not valid JSON");
        if (emptyConfig > 0)
            await output.WriteLineAsync($"Config rows with an empty Value: {emptyConfig}");

        await output.WriteLineAsync($"Photos: {photoNames.Count}{(photosMissing ? " (container not found)" : "")}");
        await output.WriteLineAsync($"HEIC/HEIF photos (browsers cannot display them): {heic.Count}");
        await WriteListAsync(output, heic);

        await output.WriteLineAsync(
            $"Tracks: {trackBlobCount} blobs in {trackBlobs.Count} trips{(tracksMissing ? " (container not found)" : "")}");
        await output.WriteLineAsync($"Orphan tracks (no trip): {orphans.Count}");
        await WriteListAsync(output, orphans);
        await output.WriteLineAsync($"Trips with TrackJson but missing track blobs: {incomplete.Count}");
        await WriteListAsync(output, incomplete);

        return unreadable.Count == 0 && unparsableConfig.Count == 0 ? 0 : 1;
    }

    /// <summary>Projde bloky kontejneru; <c>false</c>, když kontejner neexistuje (nic se nezakládá).</summary>
    private static async Task<bool> TryListAsync(
        BlobContainerClient container, Action<string, string> add, CancellationToken ct)
    {
        try
        {
            await foreach (var blob in container.GetBlobsAsync(cancellationToken: ct))
                add(blob.Name, blob.Properties.ContentType ?? "");
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404 && ex.ErrorCode == "ContainerNotFound")
        {
            return false;
        }
    }

    private static bool IsHeic(string name, string contentType) =>
        name.EndsWith(".heic", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".heif", StringComparison.OrdinalIgnoreCase)
        || contentType.StartsWith("image/heic", StringComparison.OrdinalIgnoreCase)
        || contentType.StartsWith("image/heif", StringComparison.OrdinalIgnoreCase);

    private static bool IsJson(string value)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task WriteListAsync(TextWriter output, List<string> items)
    {
        foreach (var item in items.Take(MaxListed))
            await output.WriteLineAsync($"  {item}");
        if (items.Count > MaxListed)
            await output.WriteLineAsync($"  ... and {items.Count - MaxListed} more");
    }

    /// <summary>Názvy souborů trasy v kontejneru <c>tracks</c>.</summary>
    private static class TrackFiles
    {
        public const string Gpx = "track.gpx";
        public const string GeoJson = "track.geojson";
    }

    /// <summary>Počty typů jedné vlastnosti přes všechny výlety.</summary>
    private sealed class TypeCensus
    {
        private int _int32, _int64, _double, _missing, _other;

        public void Add(TableEntity entity, string key)
        {
            if (!entity.TryGetValue(key, out var value) || value is null)
                _missing++;
            else if (value is int)
                _int32++;
            else if (value is long)
                _int64++;
            else if (value is double)
                _double++;
            else
                _other++;
        }

        public override string ToString() =>
            $"Int32 {_int32}, Int64 {_int64}, Double {_double}, missing {_missing}, other {_other}";
    }

    private sealed class TableNotFoundException(string tableName) : Exception
    {
        public string TableName { get; } = tableName;
    }
}
