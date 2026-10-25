# Přepis Baruch's Treks do .NET 10: implementační plán

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Nahradit Django aplikaci funkčně shodnou ASP.NET Core 10 aplikací bez SQLite. Data v Azure zůstávají beze změny, uživatelé budou v Table Storage, lokálně poběží Docker s vlastním Azurite a nasazovat se bude z GitHub Actions při pushi do `main`.

**Architecture:** Jeden Razor Pages projekt `src/BaruchsTreks` a jeden testovací `tests/BaruchsTreks.Tests`. Doménová logika v malých třídách podle oblastí (Trips, Tracks, Photos, Strava, Users, Storage). Stránky jsou tenké. Storage se volá jen přes repozitáře a store rozhraní, síť jen přes rozhraní (`IStravaClient`), aby je testy nahradily. Infrastruktura (Dockerfile, deploy skripty, workflow, compose) se přebírá z `C:\code\austy246\ToiletLabelsPython` a přejmenovává.

**Tech Stack:** .NET 10 (SDK podle `global.json` z ToiletLabels), ASP.NET Core Razor Pages, Azure.Data.Tables, Azure.Storage.Blobs, Azure.Extensions.AspNetCore.DataProtection.Blobs, Microsoft.Extensions.Identity.Core (`PasswordHasher<T>`), Magick.NET-Q8-x64, xunit.v3, Microsoft.AspNetCore.Mvc.Testing, Testcontainers.Azurite, Docker Desktop + buildx, PowerShell 7, bash na VPS.

**Spec:** `docs/superpowers/specs/2026-10-25-dotnet-rewrite-design.md`. Executor čte spec i tento plán. Referenční projekt: `C:\code\austy246\ToiletLabelsPython` (stejné soubory a jména tříd tam hledat jako vzor, např. `Users/`, `Storage/`, `Cli/`, `deploy/`, `tests/…/Infrastructure/`).

## Global Constraints

- Práce probíhá přímo na `main`, každý úkol se commitne a pushne. Starý workflow `deploy-vps.yml` je už smazaný (commit `5671491`), nový `deploy.yml` přibude až v úkolu 11.
- Django kód (`baruchstreks/`, `trips/`, `templates/`, `static/`, `manage.py`, `requirements.txt`, kořenový `Dockerfile`, `docker-entrypoint.sh`, `deploy/gen-env.sh`) se nemění ani nemaže až do úkolu 13. Slouží jako zdroj chování.
- Jména storage: tabulky `Trips` (stávající) a `BaruchsTreksUsers`; kontejnery `photos`, `tracks` (stávající) a `baruchstreks-keys` (privátní). V konfiguraci přepsatelná jen kvůli izolaci testů, výchozí hodnoty se nemění.
- Entita výletu: PartitionKey `Trips`, RowKey `trip_{yyyyMMddHHmmss}`. Config: PartitionKey `Config`, RowKey `strava_tokens_{uživatel}`, vlastnost `Value` = JSON. V `ParkingJson`/`HighPointJson` jsou klíče `Latitude` a **`Longtitude`** (překlep se zachovává).
- Zápis nikdy nemění typ existující vlastnosti. `ParkingJson`/`HighPointJson`/`TrackJson` jsou stringy, `TripCompletedOn` string `YYYY-MM-DD`. Úprava zachová všechny vlastnosti, které formulář neposílá (merge, ne replace).
- Bloby: fotka `photos/{tripId}/{yyyyMMddHHmmss}{přípona}`, trasa `tracks/{tripId}/track.gpx` (`application/gpx+xml`) a `tracks/{tripId}/track.geojson` (`application/geo+json`).
- GPX: `trk/trkseg/trkpt`, jinak `rte/rtept`; dekódování `utf-8-sig`, `utf-8`, `latin-1`; zjednodušení Douglas-Peucker 5 m, max 2000 bodů; práh převýšení 3 m; haversine, poloměr 6371000 m; upload max 25 MB (`25 * 1024 * 1024`).
- HEIC/HEIF se při nahrání převede na JPEG (kvalita 90, auto-orient, přípona `.jpg`); ostatní formáty beze změny.
- Limit těla požadavku 50 MB (`52_428_800`). Texty UI a CDN odkazy z Django šablon beze změny.
- Konfigurační klíče: `ConnectionStrings:Storage`, `Storage:PublicBlobBaseUrl`, `Mapy:ApiKey`, `Strava:ClientId`, `Strava:ClientSecret`, `Admin:Users:0|1:UserName|Password`, `AllowedHosts`; na VPS navíc `APP_DOMAIN`.
- Cesty beze změny: `/`, `/trips/`, `/trips/all/`, `/trips/trip/new/`, `/trips/trip/{id}/` (+ `edit/`, `copy/`, `delete/`, `track.geojson`, `track.gpx`, `track/apply/`, `track/delete/`), `/trips/strava/` (+ `connect/`, `callback/`, `disconnect/`, `import/{activityId}/`), `/accounts/login/`, `/accounts/logout/`, `/healthz`.
- Nepřihlášený i ne-admin dostane na zápisových stránkách redirect na `/accounts/login/?next=…` (nikdy 403).
- Azurite `mcr.microsoft.com/azure-storage/azurite:3.35.0` s `--skipApiVersionCheck --loose`, stejná verze lokálně i v testech. Porty hostitele: aplikace 8100, blob 10200→10000, table 10202→10002.
- Image `ghcr.io/austy246/baruchstreks-app`, tag = plné git SHA + `latest` (`latest` až po úspěšném nasazení). Shell skripty mají LF (`.gitattributes`).
- Testy nikdy nevolají síť (Strava, Mapy, geokodér) ani produkční storage. Secrets nikdy ve výpisech ani argv.
- Na sdíleném VPS nikdy `docker image prune -af` ani `system prune`.

## Review Focus

1. **Entity se smíšenými typy** (číslo jako Int32/Int64/Double, chybějící pole, prázdné stringy, `TripCompletedOn` jako string i datetime) se musí vykreslit a úprava nesmí změnit typ ani smazat pole, která formulář neposílá. Testy `LegacyEntityTests` a `UpdateTrip_PreservesUnpostedProperties` v úkolu 3.
2. **Server s českou kulturou** (`cs-CZ`): JSON mapy, GeoJSON, odkazy a parsování souřadnic používají desetinnou tečku. Test `Detail_UnderCzechCulture_UsesDotDecimals` v úkolu 7.
3. **Podivné GPX**: bez elevace a bez času, jen `rte`, kódování latin-1, nevalidní XML, 0 bodů, soubor nad 25 MB. Chyba ve formuláři, ne 500, a nic se neuloží. Testy v úkolech 2 a 8.
4. **Fotka z iPhonu**: HEIC se převede, poškozený obrázek nesmí shodit uložení výletu, požadavek nad 50 MB vrátí 413. Testy v úkolech 5 a 8.
5. **Otevřené přesměrování a cizí ID**: `?next=https://evil.example` po přihlášení vede na `/trips/`; neexistující nebo podivné ID (`does-not-exist`, `a'b`) dává 404, ne 500. Testy v úkolech 6 a 7.

---

## Mapa souborů

```
.gitattributes, .dockerignore, global.json, BaruchsTreks.slnx, Directory.Build.props   # úkol 1
docker-compose.yml, .env.example                                                       # úkol 10
src/BaruchsTreks/
  BaruchsTreks.csproj, Program.cs, Dockerfile, appsettings*.json
  Cli/HealthCheckCommand.cs (1) CheckDataCommand.cs (9) SelfTestCommand.cs (10)
  Storage/StorageOptions.cs, StorageBootstrapper.cs, TableValue.cs (3)
  Trips/Trip.cs, TripEntityMapper.cs, TripRepository.cs, TripListing.cs, TripFormData.cs (3)
  Tracks/GpxParser.cs, GpxBuilder.cs, TrackStats.cs, TrackStore.cs, TrackFieldComparer.cs (2, 4, 8)
  Photos/PhotoProcessor.cs, PhotoStore.cs (5)
  Strava/IStravaClient.cs, StravaClient.cs, StravaTokenStore.cs, StravaService.cs (9)
  Users/AppUser.cs, UserRepository.cs, AdminSeeder.cs, AuthSetup.cs (6)
  Pages/… (Index, AllTrips, TripDetail, TripEdit, TripDelete, TripCopy, TrackApply, Strava*, Login, Logout, Error, Shared/_Layout)
  wwwroot/css/style.css
tests/BaruchsTreks.Tests/
  Infrastructure/AzuriteFixture.cs, AppFactory.cs, FakeStravaClient.cs, TestStorage.cs, LegacyEntities.cs
  Fixtures/gpx/*.gpx + *.expected.json (2), sample.heic (5)
  Unit/…, Integration/…
tools/golden/make_golden.py                                                            # úkol 2 (smaže se v úkolu 13)
deploy/ deploy.ps1, docker-compose.yml, baruchstreks.caddy, remote-deploy.sh, migrate-env.sh, update-env-secrets.sh, tests/  # úkol 11
.github/workflows/deploy.yml                                                           # úkol 11
```

---

### Task 1: Kostra řešení

**Files:** Create `global.json`, `BaruchsTreks.slnx`, `Directory.Build.props`, `.gitattributes` (`*.sh eol=lf`), `src/BaruchsTreks/{BaruchsTreks.csproj,Program.cs,Cli/HealthCheckCommand.cs,appsettings.json,appsettings.Development.json}`, `tests/BaruchsTreks.Tests/{BaruchsTreks.Tests.csproj,Integration/HealthTests.cs,Infrastructure/AzuriteFixture.cs,Infrastructure/AppFactory.cs}`; Modify `.dockerignore` (bin/obj), `.gitignore`.

**Interfaces:**
- Produces: `Program` je `public partial class Program`; `AppFactory : WebApplicationFactory<Program>` s `AzuriteFixture` (sdílený kontejner, unikátní jména tabulek/kontejnerů na test třídu přes konfiguraci, falešné `IStravaClient` se doplní v úkolu 9); `HealthCheckCommand.Run(string[] args) : int` (GET `http://localhost:8080/healthz`, exit 0/1).
- Chování `args`: `--healthcheck`, `--check-data`, `--selftest` se zpracují před startem web hostu.

- [ ] **Step 1:** Přenést z ToiletLabels `global.json`, `Directory.Build.props` (net10.0, Nullable, ImplicitUsings, TreatWarningsAsErrors; `NuGetAudit` varování NU1901–NU1904 nesmí shodit build), strukturu `Program.cs` a `AzuriteFixture`/`AppFactory`, přejmenovat na BaruchsTreks.
- [ ] **Step 2:** Test `HealthTests.Healthz_ReturnsOk` (`GET /healthz` → 200, JSON `{"status":"ok"}`) a `HealthCheckCommandTests` pro exit kódy. Spustit: `dotnet test` → FAIL.
- [ ] **Step 3:** Implementovat `/healthz`, Kestrel limit 50 MB, forwarded headers, `AllowedHosts`. `dotnet test` → PASS.
- [ ] **Step 4:** Commit a push: `feat: solution skeleton, healthz`.

### Task 2: Zlaté soubory GPX a GPX parser

**Files:** Create `tools/golden/make_golden.py`, `tests/BaruchsTreks.Tests/Fixtures/gpx/*.gpx` + `*.expected.json`, `src/BaruchsTreks/Tracks/{GpxParser.cs,TrackStats.cs,GpxBuilder.cs}`, `tests/BaruchsTreks.Tests/Unit/{GpxParserTests.cs,GpxBuilderTests.cs}`. Zdroj chování: `trips/gpx_service.py`, GPX vzorky a testy v `trips/tests.py:20-200`.

**Interfaces:**
- Produces: `static class GpxParser { static ParsedTrack Parse(byte[] raw); }` vyhazuje `GpxParseError : Exception`. `ParsedTrack` obsahuje `TrackStats Stats`, `string GeoJson`, `IReadOnlyList<TrackPoint> Points`. `TrackStats` (JSON pole shodná s `TrackJson` ve stávajících datech, včetně `meters_ascend`, `meters_descend`, `duration_hours`, `started_on`, `start`, `high_point` s klíči `Latitude`/`Longtitude`; přesná jména a zaokrouhlení převzít z Pythonu a goldens). `static class GpxBuilder { static byte[] Build(string name, IEnumerable<TrackPoint> points, DateTimeOffset? start); }` (stejný výstup jako `build_gpx`). `record TrackPoint(double Lat, double Lng, double? Ele, DateTimeOffset? Time)`.

- [ ] **Step 1:** Napsat `make_golden.py`, který vezme vzorky (ze `tests.py` plus 3 reálně vypadající: trk s časem a elevací, rte bez času, trk bez elevace, latin-1) a pro každý uloží `*.expected.json` s `track_json` statistikami a GeoJSON z `gpx_service.py`. Spustit v `docker run --rm -v … python:3.12-slim` s `pip install gpxpy` (bez instalace na hostiteli). Vzorky a výstupy commitnout.
- [ ] **Step 2:** Test `GpxParserTests.Parse_MatchesGolden` (theory přes všechny fixtury, porovnání JSON s tolerancí 1e-6 u čísel), `Parse_NonGpx_Throws`, `Parse_NoPoints_Throws`, `Parse_Latin1_Decodes`, `Parse_UnderCzechCulture_UsesDots` (`CultureInfo.CurrentCulture = cs-CZ`), `Simplify_CapsAt2000Points`. FAIL.
- [ ] **Step 3:** Implementovat vlastním `System.Xml` parserem (namespace-agnostic čtení `trkpt`/`rtept`), Douglas-Peucker a statistiky podle `gpx_service.py`. Build GPX podle `build_gpx`. PASS.
- [ ] **Step 4:** Commit a push: `feat: GPX parser with golden tests`.

### Task 3: Storage, model výletu, tolerantní mapování, repozitář

**Files:** Create `src/BaruchsTreks/Storage/{StorageOptions.cs,StorageBootstrapper.cs,TableValue.cs}`, `src/BaruchsTreks/Trips/{Trip.cs,TripEntityMapper.cs,TripRepository.cs,TripListing.cs}`, `tests/BaruchsTreks.Tests/Infrastructure/{TestStorage.cs,LegacyEntities.cs}`, `tests/.../Unit/TripEntityMapperTests.cs`, `tests/.../Integration/{TripRepositoryTests.cs,LegacyEntityTests.cs,StorageBootstrapperTests.cs}`. Zdroj chování: `trips/azure_service.py` (kompletně), `trips/views.py:219-300`, `534-581`.

**Interfaces:**
- Produces: `sealed record Trip` s polem pro každou vlastnost entity (viz spec) plus `DateTimeOffset? ModifiedAt`, `DateTimeOffset? RowKeyTime`, `DateTimeOffset? CompletedAt`. `interface ITripRepository { Task<IReadOnlyList<Trip>> ListAsync(); Task<Trip?> GetAsync(string id); Task<string> CreateAsync(TripFormData data); Task UpdateAsync(string id, TripFormData data); Task MergeFieldsAsync(string id, IReadOnlyDictionary<string, object?> fields); Task DeleteAsync(string id); }`. `record TripFormData` (pole odpovídající `TripEditForm`, nullable = „neposlat"). `static class TripListing { IReadOnlyList<Trip> Filter(IEnumerable<Trip> t, string filter); IReadOnlyList<Trip> Sort(IEnumerable<Trip> t, string key, string dir); }` (`all|completed|future`, `modified|created|completed|name`, `asc|desc`, chování z `all_trips`). `interface IConfigRepository { Task<string?> GetAsync(string key); Task SaveAsync(string key, string json); Task DeleteAsync(string key); }` (partition `Config`, vlastnost `Value`). `StorageBootstrapper.EnsureAsync()` vytvoří chybějící tabulky/kontejnery, nikdy nemění přístup existujícího kontejneru, veřejné čtení `photos` jen v Development a jen u nově vytvořeného.
- `Trip.Grade` pole prázdná → `"none"` jako dnes v `get_trip_by_id`.

- [ ] **Step 1:** Testy: `Mapper_ReadsInt32Int64DoubleAndStrings`, `Mapper_MissingFields_UseDefaults`, `Mapper_TripCompletedOn_DateTimeAndString`, `Repository_List_SortedByModifiedDescending`, `UpdateTrip_PreservesUnpostedProperties` (ImageUrl, MapUrl, vlastnost navíc zůstanou), `UpdateTrip_KeepsExistingPropertyTypes`, `Create_WritesJsonFieldsAsStrings`, `Listing_FilterAndSort_MatchDjango`, `Bootstrapper_CreatesMissing_NeverChangesExistingContainerAccess`, `LegacyEntityTests` (fixtury ve stylu produkčních dat). FAIL.
- [ ] **Step 2:** Implementovat, entity číst přes slovník (`TableEntity`) s tolerantním převodem; zápis přes merge. PASS.
- [ ] **Step 3:** Commit a push: `feat: trip model, tolerant mapping, repositories, storage bootstrap`.

### Task 4: Úložiště tras a porovnání s GPX

**Files:** Create `src/BaruchsTreks/Tracks/{TrackStore.cs,TrackFieldComparer.cs}`, `tests/.../Integration/TrackStoreTests.cs`, `tests/.../Unit/TrackFieldComparerTests.cs`. Zdroj: `trips/blob_service.py:215-347`, `trips/views.py:87-216` (`track_stats`, `GPX_FIELDS`, `derived_from_track`, `compare_with_track`).

**Interfaces:**
- Produces: `interface ITrackStore { Task SaveAsync(string tripId, byte[] gpx, byte[] geojson); Task<byte[]?> GetGpxAsync(string tripId); Task<byte[]?> GetGeoJsonAsync(string tripId); Task DeleteAsync(string tripId); }` (kontejner `tracks` se při prvním zápisu vytvoří, content-type podle konstant). `static class TrackFieldComparer { static IReadOnlyDictionary<string,object> Derived(TrackStats? s); static IReadOnlyList<FieldDiff> Compare(IReadOnlyDictionary<string,object> derived, Trip current); }` s `record FieldDiff(string Field, string Label, string? Current, string New)`; popisky `Převýšení nahoru`, `Převýšení dolů`, `Délka trvání`, `Datum`, `Bod startu`, `Vrchol` v tomto pořadí; pravidla „prázdné se vyplní, shodné se přeskočí, odlišné vyžaduje potvrzení".

- [ ] **Step 1:** Testy: `Save_ThenGet_RoundTrips_WithContentTypes`, `Get_Missing_ReturnsNull`, `Delete_RemovesBoth`, `Compare_BlankFieldsAreFilled`, `Compare_EqualValuesAreNotConflicts`, `Compare_DifferentValuesAreListed`. FAIL.
- [ ] **Step 2:** Implementovat. PASS.
- [ ] **Step 3:** Commit a push: `feat: track store and GPX field comparison`.

### Task 5: Fotky a převod HEIC

**Files:** Create `src/BaruchsTreks/Photos/{PhotoProcessor.cs,PhotoStore.cs}`, `tests/.../Fixtures/sample.heic` (převzít z ToiletLabels), `tests/.../Infrastructure/TestImages.cs`, `tests/.../Unit/PhotoProcessorTests.cs`, `tests/.../Integration/PhotoStoreTests.cs`. Zdroj: `trips/blob_service.py:1-215`.

**Interfaces:**
- Produces: `static class PhotoProcessor { static ProcessedPhoto Prepare(Stream input, string fileName); }` → `record ProcessedPhoto(Stream Content, string Extension, string ContentType)`; HEIC/HEIF → JPEG q90 auto-orient `.jpg`, ostatní beze změny s content-type podle přípony (jpg, jpeg, png, gif, bmp, webp, jinak `application/octet-stream`); poškozený vstup vyhodí `PhotoException`. `interface IPhotoStore { Task<string> UploadAsync(string tripId, Stream content, string fileName); Task<IReadOnlyList<string>> ListAsync(string tripId); Task DeleteByUrlAsync(string url); }` (URL se skládají z `Storage:PublicBlobBaseUrl`; mazání rozloží URL na jméno bloku).

- [ ] **Step 1:** Testy: `Prepare_Heic_ReturnsJpeg`, `Prepare_Jpeg_IsUnchanged`, `Prepare_Png_KeepsExtensionAndContentType`, `Prepare_Corrupt_ThrowsPhotoException`, `Prepare_ExifRotated_IsOriented`, `Upload_NamesBlobWithTimestamp`, `List_ReturnsPublicUrls`, `DeleteByUrl_RemovesBlob`. FAIL.
- [ ] **Step 2:** Implementovat přes Magick.NET-Q8-x64. PASS.
- [ ] **Step 3:** Commit a push: `feat: photo store with HEIC to JPEG conversion`.

### Task 6: Uživatelé, přihlášení, Data Protection

**Files:** Create `src/BaruchsTreks/Users/{AppUser.cs,UserRepository.cs,AdminSeeder.cs,AuthSetup.cs}`, `src/BaruchsTreks/Pages/{Login,Logout}.cshtml(.cs)`, `tests/.../Integration/{AuthTests.cs,AdminSeederTests.cs}`. Zdroj: ToiletLabels `Users/*`, `trips/auth_views.py`, `templates/auth/*.html`.

**Interfaces:**
- Produces: `IUserRepository { Task<AppUser?> FindAsync(string userName); Task UpsertAsync(AppUser u); }` (tabulka `BaruchsTreksUsers`, PartitionKey `user`, RowKey lowercase). `AdminSeeder.SeedAsync()` projde `Admin:Users:*`, prázdné přeskočí, nové založí s `IsAdmin=true`, existující s nesouhlasným heslem přepíše hash. Autorizační politika `AdminOnly`; neautorizovaný požadavek (anonym i ne-admin) → redirect na `/accounts/login/?next=<cesta>`. Data Protection klíče do blobu `baruchstreks-keys`. Cookie secure za proxy.

- [ ] **Step 1:** Testy: `Login_ValidCredentials_RedirectsToNextOrTrips`, `Login_ExternalNext_RedirectsToTrips`, `Login_WrongPassword_ShowsError`, `AdminPage_Anonymous_RedirectsToLoginWithNext`, `AdminPage_NonAdmin_RedirectsToLogin`, `Logout_ClearsSession`, `Seeder_CreatesBothAdmins`, `Seeder_SkipsEmptyEntries`, `Seeder_UpdatesHashWhenPasswordChanged`, `Seeder_IsIdempotent`, `DataProtection_KeysPersistedInBlob`. FAIL.
- [ ] **Step 2:** Implementovat, texty a chybová hlášení loginu převzít z `auth_views.py` a `login.html`. PASS.
- [ ] **Step 3:** Commit a push: `feat: table-backed users, cookie auth, blob data protection`.

### Task 7: Čtecí stránky (přehled, seznam, detail, stažení trasy)

**Files:** Create `src/BaruchsTreks/Pages/{Index,AllTrips,TripDetail,Error}.cshtml(.cs)`, `Pages/Shared/_Layout.cshtml`, `Pages/_ViewImports.cshtml`, `_ViewStart.cshtml`, `wwwroot/css/style.css`, `Pages/TrackFiles` (endpointy `track.geojson`, `track.gpx`), `tests/.../Integration/{ReadPagesTests.cs,CultureTests.cs}`. Zdroj: `templates/base.html`, `templates/trips/{index,all_trips,detail,error,not_found}.html`, `static/css/style.css`, `trips/views.py:219-302`, `582-608`.

**Interfaces:**
- Consumes: `ITripRepository`, `IPhotoStore`, `ITrackStore`, `TripListing`, `TrackStats`.
- Produces: stránky se stejným HTML a texty jako Django šablony (Bootstrap z CDN, Leaflet, Mapy.cz klíč z `Mapy:ApiKey`, značky start/cíl a trasa z GeoJSON). Přehled bere prvních 6 výletů a první fotku každého. Neexistující ID → 404 stránka `not_found`.

- [ ] **Step 1:** Testy: `Index_ShowsSixNewestWithFirstPhoto`, `AllTrips_FilterAndSort`, `Detail_ShowsPhotosAndTrackStats`, `Detail_UnknownId_Returns404`, `Detail_WeirdId_Returns404`, `Detail_UnderCzechCulture_UsesDotDecimals` (cs-CZ, GeoJSON, mapa a odkazy), `TrackGeoJson_ServedToAnonymous`, `TrackGpx_IsAttachment`, `TrackMissing_Returns404`, `Layout_ShowsLoginOrLogoutLink`. FAIL.
- [ ] **Step 2:** Převést šablony 1:1 do Razor. PASS.
- [ ] **Step 3:** Commit a push: `feat: read-only trip pages`.

### Task 8: Vytvoření, úprava, kopie, mazání, nahrání GPX a fotek

**Files:** Create `src/BaruchsTreks/Pages/{TripEdit,TripCopy,TripDelete,TrackApply,TrackDelete}.cshtml(.cs)`, `Pages/Shared/_TripForm.cshtml`, `src/BaruchsTreks/Trips/TripForm.cs` (modelová vazba formuláře s volbami z `TripEditForm`), `tests/.../Integration/{TripEditTests.cs,TrackUploadTests.cs,UploadLimitTests.cs}`. Zdroj: `trips/forms.py`, `trips/views.py:27-85`, `317-532`, `610-682`, `templates/trips/{edit,confirm_delete,track_apply}.html`.

**Interfaces:**
- Consumes: všechny předchozí rozhraní, `AdminOnly`.
- Chování: všechny stránky jen `AdminOnly`; antiforgery místo `csrf_token`; `messages` → `TempData`. Nahrání GPX vyplní prázdná pole, shodné přeskočí, při odlišných hodnotách přesměruje na `TrackApply` s výběrem polí (jako dnes); chyba GPX → chyba ve formuláři a nic se neuloží; špatná přípona/nad 25 MB → chyba formuláře; fotka s chybou se jen zaloguje a výlet se uloží; kopie přidá ` (copy)` k názvu a nekopíruje fotky; mazání výletu smaže entitu (a co dnes maže, ověřit v `trip_delete`).

- [ ] **Step 1:** Testy: `Create_Anonymous_RedirectsToLogin`, `Create_ValidForm_WritesEntityWithDjangoTypes`, `Edit_PreservesUnpostedProperties`, `Edit_UnknownId_Returns404`, `Copy_AppendsCopyAndSkipsPhotos`, `Delete_RemovesTrip`, `Upload_Gpx_FillsBlankFieldsAndStoresTrack`, `Upload_Gpx_DifferentValues_GoesToApplyPage`, `Apply_WritesOnlySelectedFields`, `Upload_BrokenGpx_ShowsErrorAndSavesNothing`, `Upload_WrongExtension_Rejected`, `Upload_GpxOver25MB_Rejected`, `Upload_Heic_StoredAsJpeg`, `Upload_CorruptPhoto_TripStillSaved`, `Upload_OverRequestLimit_Returns413`, `TrackDelete_ClearsStatsAndBlobs`. FAIL.
- [ ] **Step 2:** Implementovat. PASS.
- [ ] **Step 3:** Commit a push: `feat: trip create/edit/copy/delete and uploads`.

### Task 9: Strava

**Files:** Create `src/BaruchsTreks/Strava/{IStravaClient.cs,StravaClient.cs,StravaTokenStore.cs,StravaService.cs}`, `src/BaruchsTreks/Pages/Strava{Activities,Connect,Callback,Disconnect,Import}.cshtml(.cs)`, `tests/.../Infrastructure/FakeStravaClient.cs`, `tests/.../Integration/StravaTests.cs`, `tests/.../Unit/StravaServiceTests.cs`. Zdroj: `trips/strava_service.py`, `trips/views.py:685-840`, `templates/trips/strava.html`.

**Interfaces:**
- Produces: `IStravaClient { Task<JsonDocument> PostTokenAsync(IDictionary<string,string> form); Task<JsonDocument> GetAsync(string accessToken, string path, IDictionary<string,string>? query); }`. `StravaService`: `IsConfigured`, `AuthorizeUrl(redirectUri, state)`, `ExchangeCodeAsync`, `ListActivitiesAsync(user, page, perPage)`, `ImportGpxAsync(user, activityId)` (stavba GPX z `latlng/altitude/time`, `GpxBuilder`), refresh tokenu při `expires_at - now < REFRESH_MARGIN_SECONDS` (hodnotu převzít), texty chyb 401/429 a „bez GPS" shodné. Tokeny v `IConfigRepository` pod `strava_tokens_{uživatel}`, formát beze změny. Bez `Strava:ClientId/ClientSecret` je funkce vypnutá (stránka sdělí stav jako dnes). Testovací host používá `FakeStravaClient`, nikdy skutečnou síť.

- [ ] **Step 1:** Testy: `Disabled_WhenNotConfigured`, `Callback_StoresTokensInConfigPartition`, `GetAccessToken_RefreshesWhenStale_AndSaves`, `ListActivities_MapsFields`, `Import_BuildsGpxAndRunsTrackFlow`, `Import_NoGps_ShowsError`, `Api401_ReportsReconnect`, `Api429_ReportsRateLimit`, `Pages_RequireAdmin`. FAIL.
- [ ] **Step 2:** Implementovat. PASS.
- [ ] **Step 3:** Commit a push: `feat: optional Strava import`.

### Task 10: Režim `--check-data`, `--selftest`, Docker image, lokální prostředí

**Files:** Create `src/BaruchsTreks/Cli/{CheckDataCommand.cs,SelfTestCommand.cs}`, `src/BaruchsTreks/Dockerfile`, `docker-compose.yml`, `.env.example`, `tests/.../Integration/CheckDataTests.cs`. Zdroj: ToiletLabels `Cli/`, `src/ToiletLabels/Dockerfile`, `docker-compose.yml`.

**Interfaces:**
- `CheckDataCommand.RunAsync(IConfiguration) : Task<int>` — jen čte (žádné vytváření tabulek/kontejnerů, žádný zápis): každý výlet převede, projde `Config` řádky (jen počet a parsovatelnost JSON), vypíše počet fotek (včetně nezobrazitelných HEIC) a osiřelých tras; exit 0 jen když není nečitelná entita. `SelfTestCommand` ověří načtení Magick.NET, převod `sample.heic`, `cs-CZ` kulturu a ICU (`string.Normalize`, řazení s diakritikou).
- Dockerfile: SDK `10.0`, `dotnet publish` **bez `--no-restore`**, runtime `aspnet:10.0-noble-chiseled-extra` pinnutý digestem (zjistit `docker buildx imagetools inspect`), Workstation GC, healthcheck přes `--healthcheck`. Compose: `name: baruchstreks`, služby `app` (8100→8080) a `azurite` (10200→10000, 10202→10002, volume `azurite-data`), admini z `.env`.

- [ ] **Step 1:** Testy `CheckData_ReadsLegacyEntitiesWithoutWriting` (po běhu žádná nová tabulka ani blob), `CheckData_UnreadableEntity_ExitsNonZero`. FAIL → implementovat → PASS.
- [ ] **Step 2:** Sestavit image a spustit `docker run --rm baruchstreks-app:local --selftest` → exit 0. `docker compose up -d --wait` → `curl localhost:8100/healthz` vrací 200. Změřit velikost image a paměť (`docker stats --no-stream`), zapsat do `docs/mereni.md`.
- [ ] **Step 3:** Commit a push: `feat: check-data, selftest, Docker image, local compose`.

### Task 11: Nasazení a CI

**Files:** Create `.github/workflows/deploy.yml`, `deploy/{deploy.ps1,docker-compose.yml,baruchstreks.caddy,remote-deploy.sh,migrate-env.sh,update-env-secrets.sh}`, `deploy/tests/test-migrate-env.sh`; Delete `deploy/gen-env.sh` a starý `deploy/docker-compose.yml`/`baruchstreks.caddy`/`update-env-secrets.sh` (nahrazeny; ve stejném commitu). Zdroj: odpovídající soubory z ToiletLabels.

**Interfaces:**
- Workflow: trigger push do `main`; joby `test` a `build` paralelně, `deploy` `needs: [test, build]`, `:latest` až po úspěšném nasazení; ignorovat změny jen v `docs/**` a `*.md`; secrets přes stdin, žádné v argv.
- `remote-deploy.sh`: nastaví `APP_IMAGE`, `compose pull` + `up -d --wait`, při neúspěchu návrat na předchozí tag; Caddy blok se přenačte jen při změně vlastního bloku, s `caddy validate` a rollbackem; úklid jen starých tagů `ghcr.io/austy246/baruchstreks-app`, nikdy `image prune -af`/`system prune`.
- `deploy/docker-compose.yml`: `AllowedHosts=${APP_DOMAIN};localhost`, healthcheck timeout 15 s + `start_interval`, `cpu_shares: 256`, `oom_score_adj: 500`, `mem_limit: 512m`, bez volume, alias `baruchstreks-app` v síti `web`; Caddy upstream `baruchstreks-app:8080`.
- `migrate-env.sh`: `BARUCHSTREKS_STORAGE_CONNECTION→ConnectionStrings__Storage`, `MAPY_CZ_API_KEY→Mapy__ApiKey`, `DJANGO_SUPERUSER_USERNAME/PASSWORD→Admin__Users__0__UserName/Password`, `STRAVA_*→Strava__*`, první položka `ALLOWED_HOSTS→APP_DOMAIN`, doplní `Storage__PublicBlobBaseUrl` (`https://baruchstreks.blob.core.windows.net/`, základ účtu, ne kontejneru) a `ASPNETCORE_ENVIRONMENT=Production`; staré nechá. `update-env-secrets.sh` s allow-listem včetně `ADMIN_*` → `Admin__Users__N__*`.
- `deploy.ps1 -Tag <sha>`: lokální záloha a rollback, soubory z nasazovaného commitu, vzdálená logika přes SSH stdin, SSH bez `-i`.

- [ ] **Step 1:** Přenést a přejmenovat; `deploy/tests/test-migrate-env.sh` otestuje překlad jmen (neexistující hodnoty se nepřidají, staré zůstanou, nic se nevypíše do stdout kromě stavových zpráv).
- [ ] **Step 2:** Spustit `bash deploy/tests/test-migrate-env.sh` → PASS; `actionlint` (pokud dostupný) nebo `docker run --rm rhysd/actionlint` na workflow → bez chyb.
- [ ] **Step 3:** Commit **bez pushe**. Push tohoto úkolu spustí nové CI a deploy, proto se pushne až při cutoveru (úkol 12) po souhlasu majitele.

### Task 12: Základní měření Django a cutover na VPS (provádí controller, se zastávkami)

**Files:** Create `docs/mereni.md` (doplnit), Modify `docs/migrace-vps.md` později v úkolu 13.

- [ ] **Step 1: Baseline Django** na VPS (read-only přes SSH bez `-i`): `docker stats --no-stream baruchstreks-app-1`, velikost image (`docker image ls`), 20× `curl -w %{time_total}` na `/healthz` a `/trips/`. Zapsat do `docs/mereni.md`. Nic se nemění.
- [ ] **Step 2: Stop – majitel** ověří GHCR login (`gh auth token | docker login ghcr.io -u austy246 --password-stdin`) a veřejnost balíčku `baruchstreks-app` a dodá GitHub secrets `ADMIN_USER_2`/`ADMIN_PASSWORD_2`.
- [ ] **Step 3:** `migrate-env.sh` na serveru, pak `docker compose run --rm app --check-data` s novým image (po pushi image do GHCR přes `deploy.ps1` bez nasazení nebo ručním buildem). Při problému stop.
- [ ] **Step 4: Stop – souhlas majitele s nasazením.** Pak push do `main` (spustí workflow), sledovat do úspěšného deploye.
- [ ] **Step 5:** Ověření: přehled, detail s fotkami, trasa se start/cíl, login obou adminů, restart kontejneru bez odhlášení, `/healthz`. Zápisové zkoušky jen se souhlasem majitele. Změřit novou verzi stejnými příkazy a doplnit srovnání do `docs/mereni.md`.
- [ ] **Step 6: Stop – souhlas majitele**, pak smazat volume `baruchstreks_app_data`, staré proměnné v `.env` a nepoužívané secrets.

### Task 13: Odstranění Djanga a dokumentace

**Files:** Delete `baruchstreks/`, `trips/`, `templates/`, `static/`, `manage.py`, `requirements.txt`, kořenový `Dockerfile`, `docker-entrypoint.sh`, `tools/golden/`; Modify `README.md`, `CLAUDE.md` (nový), `docs/migrace-vps.md` → `docs/provoz-vps.md`.

- [ ] **Step 1:** Až po schválení výsledku cutoveru majitelem odstranit Django soubory (golden fixtury v testech zůstávají).
- [ ] **Step 2:** Přepsat dokumentaci podle `ToiletLabelsPython/docs/provoz-vps.md` a `CLAUDE.md`: provoz, rollback `deploy.ps1 -Tag`, secrets, DNS, odstranění z VPS.
- [ ] **Step 3:** `dotnet test` → PASS; commit a push: `chore: remove Django app after .NET cutover; update docs`.

---

## Self-review

- **Pokrytí specu:** aplikace/cesty (7, 8), oprávnění (6), data a kompatibilita (3), fotky/HEIC (5), trasy (2, 4, 8), Strava (9), konfigurace (Global Constraints), lokální prostředí (10), testy (průběžně, Review Focus), image/selftest (10), nasazení (11), cutover a měření (12), úklid (13).
- **Typy:** `ITripRepository`, `ITrackStore`, `IPhotoStore`, `IConfigRepository`, `IStravaClient`, `TrackStats`, `TripFormData`, `FieldDiff` jsou definované v úkolech před jejich použitím.
- **Pořadí:** úkol 11 se commituje bez pushe, aby nová CI nenasadila před souhlasem majitele.
