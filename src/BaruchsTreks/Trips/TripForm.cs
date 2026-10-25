using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace BaruchsTreks.Trips;

/// <summary>
/// Formulář úprav výletu (port <c>TripEditForm</c> z <c>trips/forms.py</c>): jména polí, výběry, validace a texty
/// chyb jsou stejné jako v Django. Hodnoty se drží jako text z požadavku; <see cref="Validate"/> je rozparsuje
/// (invariantní kultura, takže česká kultura serveru nic nemění).
/// </summary>
public sealed partial class TripForm
{
    /// <summary>Soubor GPX smí mít nejvýš 25 MB.</summary>
    public const long MaxGpxUploadSize = 25 * 1024 * 1024;

    public const int TitleMaxLength = 200;

    public static readonly IReadOnlyList<(string Value, string Label)> UiaaGradeChoices =
    [
        ("", "-- Select Grade --"),
        .. new[]
        {
            "I", "II", "III", "III+", "IV-", "IV", "IV+", "V-", "V", "V+", "VI-", "VI", "VI+", "VII-", "VII", "VII+",
            "VIII-", "VIII", "VIII+", "IX-", "IX", "IX+", "X-", "X", "X+", "XI-", "XI", "XI+", "XII-", "XII",
        }.Select(g => (g, g)),
    ];

    public static readonly IReadOnlyList<(string Value, string Label)> AlpineGradeChoices =
    [
        ("", "-- Select Grade --"),
        ("F", "F (Facile/Easy)"),
        ("F+", "F+ (Facile+/Easy+)"),
        ("PD-", "PD- (Peu Difficile-/Slightly Difficult-)"),
        ("PD", "PD (Peu Difficile/Slightly Difficult)"),
        ("PD+", "PD+ (Peu Difficile+/Slightly Difficult+)"),
        ("AD-", "AD- (Assez Difficile-/Fairly Difficult-)"),
        ("AD", "AD (Assez Difficile/Fairly Difficult)"),
        ("AD+", "AD+ (Assez Difficile+/Fairly Difficult+)"),
        ("D-", "D- (Difficile-/Difficult-)"),
        ("D", "D (Difficile/Difficult)"),
        ("D+", "D+ (Difficile+/Difficult+)"),
        ("TD-", "TD- (Très Difficile-/Very Difficult-)"),
        ("TD", "TD (Très Difficile/Very Difficult)"),
        ("TD+", "TD+ (Très Difficile+/Very Difficult+)"),
        ("ED1", "ED1 (Extrêmement Difficile 1/Extremely Difficult 1)"),
        ("ED2", "ED2 (Extrêmement Difficile 2/Extremely Difficult 2)"),
        ("ED3", "ED3 (Extrêmement Difficile 3/Extremely Difficult 3)"),
        ("ED4", "ED4 (Extrêmement Difficile 4/Extremely Difficult 4)"),
        ("ED5", "ED5 (Extrêmement Difficile 5/Extremely Difficult 5)"),
        ("ABO", "ABO (Abominably Difficult)"),
    ];

    public static readonly IReadOnlyList<(string Value, string Label)> TripClassChoices =
    [
        ("", "--Vyber kategorii--"),
        ("none", "none"),
        ("Mountaineering", "VHT"),
        ("Skialp", "Skialpy"),
        ("Slope", "Sjezdovka"),
        ("Climb", "Lezení"),
        ("Run", "Běh"),
        ("Ferratta", "Via ferrata"),
        ("Trail", "Trek"),
    ];

    public static readonly IReadOnlyList<(string Value, string Label)> FerataGradeChoices =
    [
        ("", "-- Select Grade --"),
        ("A", "A (Easy)"),
        ("B", "B (Moderate)"),
        ("C", "C (Difficult)"),
        ("D", "D (Very Difficult)"),
        ("E", "E (Extremely Difficult)"),
        ("F", "F (Exceptionally Difficult)"),
    ];

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "MM/dd/yyyy", "MM/dd/yy"];

    private readonly Dictionary<string, List<string>> _errors = [];

    // Hodnoty polí tak, jak je pošle (nebo vykreslí) formulář.
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string TripCompletedOn { get; set; } = "";
    public string LengthHours { get; set; } = "";
    public string Participants { get; set; } = "";
    public string MetersAscend { get; set; } = "";
    public string MetersDescend { get; set; } = "";
    public string UiaaGrade { get; set; } = "";
    public string AlpineGrade { get; set; } = "";
    public string TripClass { get; set; } = "";
    public string FerataGrade { get; set; } = "";
    public string ParkingJson { get; set; } = "";
    public string HighPointJson { get; set; } = "";

    /// <summary>Nahrávaný GPX, jen když byl vybrán (prázdná část formuláře se bere jako žádný soubor).</summary>
    public IFormFile? GpxFile { get; private set; }

    /// <summary>Nahrávané fotky (bez prázdných částí).</summary>
    public IReadOnlyList<IFormFile> Photos { get; private set; } = [];

    // Rozparsované hodnoty, platné po úspěšném Validate().
    public DateOnly? ParsedDate { get; private set; }
    public double? ParsedLengthHours { get; private set; }
    public int? ParsedMetersAscend { get; private set; }
    public int? ParsedMetersDescend { get; private set; }

    public bool IsBound { get; private set; }

    public IReadOnlyList<string> ErrorsFor(string field) =>
        _errors.TryGetValue(field, out var list) ? list : [];

    public bool HasErrors => _errors.Count > 0;

    /// <summary>Formulář z požadavku (po odeslání).</summary>
    public static TripForm FromRequest(IFormCollection form)
    {
        string Get(string name) => form.TryGetValue(name, out var v) ? v.ToString() : "";

        return new TripForm
        {
            IsBound = true,
            // CharField v Django ořezává okolní mezery.
            Title = Get("title").Trim(),
            Description = Get("description").Trim(),
            TripCompletedOn = Get("trip_completed_on").Trim(),
            LengthHours = Get("length_hours").Trim(),
            Participants = Get("participants").Trim(),
            MetersAscend = Get("meters_ascend").Trim(),
            MetersDescend = Get("meters_descend").Trim(),
            UiaaGrade = Get("uiaa_grade"),
            AlpineGrade = Get("alpine_grade"),
            TripClass = Get("trip_class"),
            FerataGrade = Get("ferata_grade"),
            ParkingJson = Get("parking_json").Trim(),
            HighPointJson = Get("high_point_json").Trim(),
            GpxFile = form.Files.GetFile("gpx_file") is { FileName.Length: > 0 } gpx ? gpx : null,
            Photos = form.Files.GetFiles("photos").Where(f => f.Length > 0).ToList(),
        };
    }

    /// <summary>Počáteční hodnoty pro úpravu existujícího výletu (jako <c>initial_data</c> ve <c>trip_edit</c>).</summary>
    public static TripForm FromTrip(Trip trip) => new()
    {
        Title = trip.Title,
        Description = trip.Description,
        TripCompletedOn = trip.TripCompletedOn.Length > 10 ? trip.TripCompletedOn[..10] : trip.TripCompletedOn,
        LengthHours = trip.LengthHoursText,
        Participants = trip.Participants,
        MetersAscend = DjangoFormat.Number(trip.MetersAscend),
        MetersDescend = DjangoFormat.Number(trip.MetersDescend),
        UiaaGrade = trip.UiaaGrade,
        AlpineGrade = trip.AlpineGrade,
        TripClass = trip.TripClass,
        FerataGrade = trip.FerataGrade,
        ParkingJson = trip.ParkingJson,
        HighPointJson = trip.HighPointJson,
    };

    /// <summary>Zkontroluje pole a naplní chyby jako Django <c>form.is_valid()</c>.</summary>
    public bool Validate()
    {
        _errors.Clear();

        if (Title.Length == 0)
            AddError("title", "This field is required.");
        else if (Title.Length > TitleMaxLength)
            AddError("title", $"Ensure this value has at most {TitleMaxLength} characters (it has {Title.Length}).");

        ParsedDate = ParseDate(TripCompletedOn);
        ParsedLengthHours = ParseFloat("length_hours", LengthHours);
        ParsedMetersAscend = ParseInt("meters_ascend", MetersAscend);
        ParsedMetersDescend = ParseInt("meters_descend", MetersDescend);

        CheckChoice("uiaa_grade", UiaaGrade, UiaaGradeChoices);
        CheckChoice("alpine_grade", AlpineGrade, AlpineGradeChoices);
        CheckChoice("trip_class", TripClass, TripClassChoices);
        CheckChoice("ferata_grade", FerataGrade, FerataGradeChoices);

        CheckGpx();
        return !HasErrors;
    }

    /// <summary>Data pro zápis; <c>null</c> = pole se neposílá (viz <see cref="TripFormData"/>).</summary>
    public TripFormData ToData() => new()
    {
        Title = Title,
        Description = Description,
        TripCompletedOn = ParsedDate,
        LengthHours = ParsedLengthHours,
        Participants = Participants,
        MetersAscend = ParsedMetersAscend,
        MetersDescend = ParsedMetersDescend,
        UiaaGrade = UiaaGrade,
        AlpineGrade = AlpineGrade,
        TripClass = TripClass,
        FerataGrade = FerataGrade,
        ParkingJson = ParkingJson,
        HighPointJson = HighPointJson,
    };

    /// <summary>
    /// Hodnoty formuláře jako <see cref="Trip"/>, aby je šlo porovnat s hodnotami z GPX
    /// (<c>compare_with_track(derived, trip_data)</c> porovnává s odeslaným formulářem, ne s uloženým výletem).
    /// </summary>
    public Trip ToComparable() => new()
    {
        TripCompletedOn = ParsedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
        LengthHours = ParsedLengthHours ?? 0,
        MetersAscend = ParsedMetersAscend ?? 0,
        MetersDescend = ParsedMetersDescend ?? 0,
        ParkingJson = ParkingJson,
        HighPointJson = HighPointJson,
    };

    private void AddError(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list))
            _errors[field] = list = [];
        list.Add(message);
    }

    private DateOnly? ParseDate(string text)
    {
        if (text.Length == 0)
            return null;
        if (DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        AddError("trip_completed_on", "Enter a valid date.");
        return null;
    }

    private double? ParseFloat(string field, string text)
    {
        if (text.Length == 0)
            return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            AddError(field, "Enter a number.");
            return null;
        }

        if (value < 0)
        {
            AddError(field, "Ensure this value is greater than or equal to 0.");
            return null;
        }

        return value;
    }

    private int? ParseInt(string field, string text)
    {
        if (text.Length == 0)
            return null;
        // Django IntegerField zahodí koncové ".0" (5.0 je 5).
        var cleaned = TrailingZeroDecimal().Replace(text, "");
        if (!int.TryParse(cleaned, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            AddError(field, "Enter a whole number.");
            return null;
        }

        if (value < 0)
        {
            AddError(field, "Ensure this value is greater than or equal to 0.");
            return null;
        }

        return value;
    }

    private void CheckChoice(string field, string value, IReadOnlyList<(string Value, string Label)> choices)
    {
        if (value.Length > 0 && !choices.Any(c => c.Value == value))
            AddError(field, $"Select a valid choice. {value} is not one of the available choices.");
    }

    private void CheckGpx()
    {
        if (GpxFile is not { } file)
            return;

        if (file.Length == 0)
        {
            AddError("gpx_file", "The submitted file is empty.");
        }
        else if (!file.FileName.EndsWith(".gpx", StringComparison.OrdinalIgnoreCase))
        {
            AddError("gpx_file", "Nahraj soubor ve formátu .gpx (v Garmin Connect: aktivita → ⚙ → Export to GPX).");
        }
        else if (file.Length > MaxGpxUploadSize)
        {
            AddError("gpx_file", $"GPX soubor je příliš velký (max {MaxGpxUploadSize / (1024 * 1024)} MB).");
        }
    }

    [GeneratedRegex(@"\.0*\s*$")]
    private static partial Regex TrailingZeroDecimal();
}
