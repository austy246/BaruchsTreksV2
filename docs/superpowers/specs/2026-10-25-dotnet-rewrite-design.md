# Přepis Baruch's Treks do .NET 10: návrh

Datum: 2026-10-25
Stav: návrh ke schválení

Referenční projekt: `C:\code\austy246\ToiletLabelsPython` (hotový přepis stejného typu). Kód, struktura, nasazení i provozní poučení se berou odtamtud:
`docs/superpowers/specs/2026-10-24-dotnet-rewrite-design.md`, `docs/superpowers/plans/2026-10-24-dotnet-rewrite.md`, `.github/workflows/deploy.yml`, `deploy/`, `src/ToiletLabels/Dockerfile`, `docker-compose.yml`, `docs/provoz-vps.md`, `CLAUDE.md`.

## Záměr a proč

Aplikaci Baruch's Treks (deník horských výletů, Django 5.x, běží na sdíleném VPS na `https://treks.austy.cz`) přepíšeme do ASP.NET Core na .NET 10 (LTS) stejným způsobem jako ToiletLabels. Důvody jsou stejné: menší spotřeba prostředků na sdíleném VPS, stabilní a předvídatelný build, žádný lokální stav (SQLite a volume) a jednotný způsob nasazení napříč projekty.

## Cíle

1. Funkční parita se současnou aplikací: přehled, všechny výlety, detail, vytvoření, úprava, kopie a mazání výletu, fotky, GPX trasy na mapě (Mapy.cz) se značkami start a cíl, stažení trasy, nahrání a smazání GPX, volitelný import ze Stravy, přihlášení a odhlášení, `/healthz`. UI a texty beze změny.
2. Nová aplikace čte stávající produkční data beze změny formátu a bez migrace (tabulka `Trips`, kontejnery `photos` a `tracks` na účtu `baruchstreks`).
3. Žádné SQLite. Uživatelé jsou v Table Storage, přihlášení přežije restart i nové nasazení.
4. `docker compose up` spustí lokálně kompletní prostředí s vlastním Azurite.
5. Nasazení jako u ToiletLabels: GitHub Actions při každém pushi do `main`, na serveru `remote-deploy.sh` s automatickým návratem, `deploy.ps1` jako lokální záloha a rollback.

## Ne-cíle

- Žádné nové funkce ani změna vzhledu a textů (včetně dnešního mixu angličtiny a češtiny).
- Žádná správa uživatelů v UI, registrace ani obnova hesla.
- Žádný frontendový build: Bootstrap, Bootstrap Icons, Leaflet a podobné zůstanou z CDN jako dnes.
- Žádná migrace ani úprava existujících entit, fotek a tras.
- Django `/admin/` a diagnostické stránky `debug/` a `debug-logging/` se nepřenášejí *(rozhodnutí návrhu, majitel může změnit)*. Django model `Trip` (`trips/models.py`) je mrtvý kód, nikde se nepoužívá a nepřenáší se.

## Anti-cíle (selhání, i kdyby to technicky fungovalo)

- Nová aplikace poškodí, přepíše nebo přestane číst produkční výlety, fotky či trasy.
- Lokální vývoj nebo testy sahají na produkční storage nebo na síť (Strava, geokodér, Mapy.cz).
- Image nebo běžící kontejner je těžší než dnešní Python verze (měří se před přechodem).
- Admin je po každém nasazení nebo restartu odhlášen.
- Deploy nebo úklid smaže cokoli, co nepatří tomuto projektu na sdíleném VPS (žádné `docker image prune -af`, žádné `system prune`).
- Tajné hodnoty se objeví ve výpisech, logu nebo argv.

## Rozhodnuté majitelem

- .NET 10 LTS, ASP.NET Core Razor Pages. Pracuje se přímo na `main` v repu `austy246/BaruchsTreksV2` (žádné nové repo, Django kód se odstraní až po úspěšném přechodu).
- Dva uživatelé, oba admini, **oba zakládaní z GitHub secrets**. Žádní neadminští uživatelé dnes neexistují.
- Strava tokeny zůstávají v Table Storage tam, kde jsou dnes.
- **HEIC se převádí na JPEG na serveru** (Magick.NET, varianta 2). Staré HEIC bloby se nemění.
- **GPX vlastním parserem nad `System.Xml`**, bez další knihovny, s parametry dnešního `gpx_service.py`.
- Doména zůstává `treks.austy.cz`.

## Technické řešení

### Aplikace

- ASP.NET Core 10, Razor Pages, jeden webový projekt `src/BaruchsTreks` a jeden testovací projekt.
- Cesty zůstávají shodné s dnešními (odkazy a záložky fungují dál), kromě zrušených: `/` → redirect na `/trips/`, `/trips/`, `/trips/all/`, `/trips/trip/new/`, `/trips/trip/{id}/`, `.../edit/`, `.../copy/`, `.../delete/`, `.../track.geojson`, `.../track.gpx`, `.../track/apply/`, `.../track/delete/`, `/trips/strava/`, `.../connect/`, `.../callback/`, `.../disconnect/`, `.../import/{activityId}/`, `/accounts/login/`, `/accounts/logout/`, `/healthz`. Odkaz Strava callback musí zůstat na stejné cestě, protože je zaregistrovaný u Stravy (callback domain `treks.austy.cz`).
- Šablony (`templates/`) se převedou z Django šablon 1:1 včetně inline CSS a JS. `static/css/style.css` se přenese.
- Oprávnění: čtení je veřejné (přehled, seznam, detail, mapa, stažení trasy). Vytvoření, úprava, kopie, mazání, trasa apply/delete a Strava jsou jen pro `IsAdmin`. Nepřihlášený i přihlášený bez adminu je přesměrován na `/accounts/login/?next=…` (dnešní `login_required` + `user_passes_test` nikdy nevrací 403). Dnes je admin `is_staff or is_superuser`; v nové aplikaci `IsAdmin`. `trip_delete` a `trip_copy` mají ve zdroji `is_staff`, stejně jako ostatní zápisové akce se mapují na `IsAdmin`. Zpracování `next` se převezme z `auth_views.py`.
- Limit nahrávání 50 MB (shodně s Caddy), GPX navíc max 25 MB jako dnes (`MAX_GPX_UPLOAD_SIZE`).
- Za Caddy: forwarded headers, důvěřovat proxy v Docker síti, secure cookies, `AllowedHosts = <doména>;localhost`.

### Data (Azure Table + Blob)

Jména se zachovají (staré) nebo mají předponu projektu (nová):

| Zdroj | Jméno | Poznámka |
|---|---|---|
| Tabulka výletů a konfigurace | `Trips` | stávající. Partition `Trips` = výlety, partition `Config` = klíč/hodnota (Strava tokeny) |
| Tabulka uživatelů | `BaruchsTreksUsers` | nová |
| Kontejner fotek | `photos` | stávající, bloby `{tripId}/{yyyyMMddHHmmss}{přípona}` |
| Kontejner tras | `tracks` | stávající, bloby `{tripId}/track.gpx` a `{tripId}/track.geojson` |
| Kontejner klíčů | `baruchstreks-keys` | nový, privátní, ASP.NET Data Protection |

**Entita výletu** (PartitionKey `Trips`, RowKey = `trip_{yyyyMMddHHmmss}…`). Vlastnosti: `Title`, `Description`, `TripCompletedOn` (string ISO nebo datetime), `LengthHours`, `Location`, `ElevationGain`, `Difficulty`, `MapUrl`, `ImageUrl`, `Participants`, `MetersAscend`, `MetersDescend`, `UiaaGrade`, `AlpineGrade`, `TripClass`, `FerataGrade`, `ParkingJson`, `HighPointJson`, `TrackJson`. Klíče uvnitř `ParkingJson` a `HighPointJson` jsou `Latitude` a **`Longtitude` (překlep zůstává)**, nesmí se opravit.

Pravidla kompatibility:

- Entity se čtou **tolerantně** přes slovník s převodem typů a výchozími hodnotami (čísla mohou být Int32/Int64/Double, chybějící pole, prázdné stringy, `TripCompletedOn` jako string nebo datetime, grade pole prázdné → hodnota `none` jako dnes). Žádné striktně typované modely.
- Pořadí seznamu: `modified_at` (Table `Timestamp`) sestupně, fallback na čas odvozený z RowKey, jinak nejstarší. Chování `index` (omezený výběr) a `all_trips` (filtry) se převezme z `views.py` doslova.
- Zápis při úpravě zachovává pole, která formulář neposílá (dnes `update_entity` s merge). `ImageUrl`, `MapUrl` a ostatní nezobrazená pole se nesmí smazat.
- Konfigurace: řádek (`Config`, `strava_tokens_{uživatel}`) s vlastností `Value` obsahující JSON tokenů (`access_token`, `refresh_token`, `expires_at`, `athlete`…). Formát se nemění. Refresh tokenu, když zbývá méně než `REFRESH_MARGIN_SECONDS`.
- Režim `--check-data` *(rozhodnutí návrhu)*: jen čte, projde všechny výlety, config řádky, seznam fotek a tras, každou entitu zkusí převést a vypíše souhrn problémů (včetně HEIC blobů a osiřelých bloků). Nikdy nezapisuje, nevytváří tabulky ani kontejnery. Spouští se na VPS před přechodem: `docker compose run --rm app --check-data`. Nečitelná entita zastaví přechod.

**Fotky.** Nahraje se originál pod novým jménem `{tripId}/{timestamp}{přípona}` s content-type podle přípony/obsahu. **HEIC/HEIF se převede na JPEG** (kvalita 90, otočení podle EXIF, přípona `.jpg`) před uložením. Ostatní formáty se ukládají nezměněné. Selhání převodu vrátí uživateli srozumitelnou chybu u formuláře a výlet se přesto uloží (jako dnes se chyba nahrání fotky jen zaloguje). Mazání fotky podle URL se zachová. Veřejná URL fotek je samostatné nastavení (`Storage:PublicBlobBaseUrl`), protože lokálně se aplikace připojuje na `http://azurite:10000/...` a prohlížeč potřebuje `http://localhost:<port>/...`.

**Trasy.** Při nahrání GPX se uloží originál a zjednodušený GeoJSON (`track.gpx`, `track.geojson`), do entity jde `TrackJson` se statistikami. Parser: `trk/trkseg/trkpt`, a když nejsou body, `rte/rtept`; kódování `utf-8-sig`, `utf-8`, `latin-1`. Zjednodušení Douglas-Peucker na 5 m, strop 2000 bodů, vzdálenost haversine (ground distance), převýšení s prahem 3 m. Výstupy musí odpovídat dnešnímu `gpx_service.py` (testy se stejnými vzorky). Chybný soubor vrací srozumitelnou chybu (dnešní `GpxParseError`). Kontejner `tracks` se při prvním zápisu vytvoří, pokud chybí.

**Uživatel** (PartitionKey `user`, RowKey = uživatelské jméno lowercase). `UserName`, `PasswordHash` (`PasswordHasher<T>` z `Microsoft.Extensions.Identity.Core`, bez celého Identity a EF), `IsAdmin`, `Created`. Při startu se idempotentně založí oba admini z konfigurace (`Admin__Users__0__UserName/Password`, `Admin__Users__1__…`, v GitHub secrets `ADMIN_USER_1/ADMIN_PASSWORD_1`, `ADMIN_USER_2/ADMIN_PASSWORD_2`). Prázdný secret = uživatel se nezakládá. *(Rozhodnutí návrhu, majitel může změnit)* GitHub secrets jsou zdroj pravdy: pokud heslo ze secretu neodpovídá uloženému hashi, hash se při startu přepíše. Přihlášení je cookie autentizace; hlášení chyb loginu a texty se převezmou z `auth_views.py` a `templates/auth/login.html`.

**Data Protection** klíče v blobu `baruchstreks-keys` (`Azure.Extensions.AspNetCore.DataProtection.Blobs`).

**Vytváření zdrojů** *(rozhodnutí návrhu)*: při startu se idempotentně vytvoří chybějící tabulky `Trips` a `BaruchsTreksUsers` a kontejnery `photos`, `tracks` a `baruchstreks-keys` (poslední privátní). Přístupovou úroveň existujících kontejnerů aplikace nikdy nemění. Veřejné čtení nově vytvořeného `photos` nastavuje jen v `Development`.

### Strava

Import je volitelný: bez `Strava__ClientId` a `Strava__ClientSecret` se funkce vypne, GPX upload funguje dál. Chování se převezme ze `strava_service.py`: OAuth connect/callback/disconnect, seznam vlastních aktivit, import GPX sestavením z `latlng`, `altitude`, `time` streamů, texty chyb 401 a 429. Všechna síťová volání jdou přes rozhraní, které testy nahrazují falešnou implementací. Test host nikdy nevolá skutečnou Stravu.

### Konfigurace (proměnné prostředí)

| Proměnná | Účel |
|---|---|
| `ConnectionStrings__Storage` | connection string účtu (lokálně Azurite) |
| `Storage__PublicBlobBaseUrl` | základ URL fotek pro prohlížeč |
| `Mapy__ApiKey` | klíč Mapy.cz (v prohlížeči) |
| `Strava__ClientId`, `Strava__ClientSecret` | volitelné |
| `Admin__Users__N__UserName` / `__Password` | dva admini |
| `ASPNETCORE_ENVIRONMENT` | `Development` lokálně, `Production` na VPS |
| `APP_DOMAIN` | `treks.austy.cz`, zdroj pro `AllowedHosts` i Caddy blok |

Staré názvy (`BARUCHSTREKS_STORAGE_CONNECTION`, `MAPY_CZ_API_KEY`, `DJANGO_*`, `STRAVA_*`, `ALLOWED_HOSTS`, `CSRF_TRUSTED_ORIGINS`, `SECRET_KEY`) převede `migrate-env.sh` na nové; staré zůstanou do ověření a smažou se po souhlasu majitele.

### Lokální prostředí

`docker-compose.yml` v kořeni, `name: baruchstreks`, kontejnery `baruchstreks-app-1` a `baruchstreks-azurite-1`, volume `baruchstreks_azurite-data`. Porty hostitele *(rozhodnutí návrhu)*: aplikace `8100`, Azurite blob `10200→10000`, table `10202→10002`. Nesmí kolidovat s ToiletLabels (10100/10102) ani FoxLordem (10000–10002). Admini pro lokál z `.env` (gitignored), ukázka v `.env.example`.

### Testy

- xunit.v3 + Testcontainers Azurite, každý běh má vlastní dočasný Azurite. Test host nikdy nesáhne na produkční storage ani na síť (falešný geokodér/Strava; v tomto projektu je geokódování případné jen tam, kde už dnes je).
- Jednotkové testy: tolerantní čtení entit (chybějící/smíšené typy), `Longtitude`, řazení, GPX parser a statistiky (stejné vzorky jako dnes), HEIC převod (vzorek), fold chování pod českou kulturou (`cs-CZ`: desetinná tečka v JSON a URL, bez čárky).
- Integrační testy přes `WebApplicationFactory`: oprávnění (anonym → login s `next`, ne-admin → 403), login/logout, vytvoření, úprava (zachování nezobrazených polí), kopie, mazání, upload fotky vč. HEIC, upload/smazání trasy, stažení trasy, Strava flow s falešnou službou, `/healthz`, založení obou adminů při startu a aktualizace hesla ze secretu.
- Testy z `trips/tests.py` se přenesou podle chování, ne řádek po řádku.

### Image

- Multi-stage Dockerfile v `src/BaruchsTreks/Dockerfile`: SDK `10.0` pro publish (`dotnet publish` **bez `--no-restore`**, protože gha cache nepřenáší NuGet cache mount), runtime `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra` (ICU kvůli diakritice), base image pinnuté digestem.
- `--selftest` v image ověří nativní knihovnu Magick.NET (načtení, dekódování a převod HEIC vzorku), českou kulturu a ICU.
- NuGet audit varování (NU1901–1904) nesmí shodit build.
- Workstation GC, `mem_limit` 512 MB, dokud měření neukáže, že stačí méně. Produkční compose: `cpu_shares: 256`, `oom_score_adj: 500`. Healthcheck přes `--healthcheck` v aplikaci, timeout 15 s plus `start_interval`.
- Image `ghcr.io/austy246/baruchstreks-app` (stávající balíček), tagy = krátký SHA + `latest` (`latest` až po úspěšném nasazení).

### Nasazení

Převzít a přejmenovat z ToiletLabels: `.github/workflows/deploy.yml`, `deploy/remote-deploy.sh`, `deploy/migrate-env.sh`, `deploy/update-env-secrets.sh`, `deploy/deploy.ps1`, `deploy/docker-compose.yml`, `deploy/baruchstreks.caddy` (upstream `baruchstreks-app:8080`).

- Workflow při každém pushi do `main`: test a build paralelně, deploy čeká na oba, `:latest` až po úspěšném nasazení. SSH klíč `baruchstreks-ci` (secret `SSH_KEY`).
- Na serveru `remote-deploy.sh`: nastaví `APP_IMAGE`, `compose pull` a `up -d --wait`, při neúspěchu se automaticky vrátí na předchozí tag. Caddy blok se přenačítá jen při změně vlastního bloku, s `caddy validate` a rollbackem.
- Úklid: pouze staré tagy vlastního image, nikdy `docker image prune -af` ani `system prune`.
- GitHub secrets jsou zdroj pravdy a synchronizují se na server přes stdin (`update-env-secrets.sh` s allow-listem). Nikdy v argv ani výpisu.
- `deploy.ps1` je lokální záloha a rollback (`-Tag <sha>`). Vzdálená logika se posílá přes SSH stdin a bere soubory z nasazovaného commitu, takže `-Tag` funguje i pro poslední Django commit.
- GHCR login lokálně: `gh auth token | docker login ghcr.io -u austy246 --password-stdin` (token potřebuje `write:packages`). SSH na VPS bez `-i` (ssh-agent).

### Přechod v produkci (cutover)

Provádí agent. **Před nasazením a před mazáním čehokoli (volume `app_data`, staré proměnné, secrets) se zastaví a čeká na výslovný souhlas majitele.**

0. **Úplně první commit** smaže starý `deploy-vps.yml` a pushne se samostatně. Nový workflow se přidá až s hotovou aplikací (jinak by push spustil starou CI s novými deploy soubory).
1. **Baseline Django verze** před přechodem: paměť kontejneru (`docker stats`), odezva (např. 20× `/trips/` a `/healthz`), velikost image. Uloží se do `docs/` pro srovnání.
2. Majitel jednorázově ověří GHCR login a viditelnost balíčku.
3. `migrate-env.sh` přeloží stará jména na nová, staré nechá.
4. `--check-data` proti produkci. Při problému stop.
5. Majitelův souhlas, pak nasazení nové verze do stejného compose projektu `baruchstreks`.
6. Ověření: přehled, detail s fotkami a trasou (start/cíl), přihlášení obou adminů, restart kontejneru bez odhlášení, `/healthz`. Zápisové zkoušky (upload, edit) jen se souhlasem majitele a po nich ruční úklid testovacího výletu.
7. Měření nové verze (stejné metriky jako baseline) a srovnání.
8. Majitelův souhlas, pak smazání volume `app_data`, starých proměnných a nepoužívaných secrets.
9. Django kód (`baruchstreks/`, `trips/`, `manage.py`, `requirements.txt`, starý `Dockerfile`, `docker-entrypoint.sh`, `deploy/gen-env.sh`) se z repa odstraní až po úspěšném přechodu. Aktualizovat `README.md`, `CLAUDE.md`, `docs/migrace-vps.md` → `docs/provoz-vps.md`.

## Rozhodnutí z kontroly návrhu (moje, majitel může změnit)

- **Rollback na Django musí zůstat možný až do smazání volume.** Proto nová aplikace zapisuje stejné typy jako Django: `ParkingJson`, `HighPointJson`, `TrackJson` jako string s JSON, `TripCompletedOn` jako string ISO `YYYY-MM-DD`, čísla jako Int/Double podle toho, co už entita měla, ostatní jako string. Změna typu existující vlastnosti je zakázaná. Test: entita zapsaná .NET se přečte dnešním Python kódem (golden test) a naopak. Uživatelé jsou v samostatné tabulce, takže rollback neovlivní.
- **Golden soubory GPX.** Před odstraněním Pythonu se z vzorků v `trips/tests.py` a z několika reálných GPX vygenerují referenční výstupy (`TrackJson`, GeoJSON, statistiky) dnešním `gpx_service.py` a uloží do testovacích dat. Parser v .NET se testuje proti nim.
- **Parita UI.** Šablony se převádějí 1:1. Django `messages` → `TempData`, `{% csrf_token %}` → antiforgery, Django filtry `date`/`floatformat` → stejný výstup pod `cs-CZ` i `en` (ověřit v šablonách). Ověření: obě verze běží lokálně vedle sebe nad stejným Azurite a porovná se HTML hlavních stránek (text a struktura); finální vizuální kontrolu dělá majitel před smazáním Djanga.
- **Staré HEIC fotky** dnes nemají žádné zvláštní zobrazení, takže není co přenášet. `--check-data` je jen vypíše.
- **Uživatelé a secrets.** Existující GitHub secrets: `SSH_HOST`, `SSH_USER`, `SSH_KEY`, `APP_DOMAIN`, `BARUCHSTREKS_STORAGE_CONNECTION`, `MAPY_CZ_API_KEY`, `DJANGO_SUPERUSER_USERNAME/PASSWORD`. První admin = hodnoty z `DJANGO_SUPERUSER_*` (`migrate-env.sh`, resp. přejmenování secretů), druhý admin potřebuje `ADMIN_USER_2` a `ADMIN_PASSWORD_2`, které **musí dodat majitel** (agent hesla nezná ani nezadává). Bez nich se nasadí jen první admin. `STRAVA_*` secrets dnes neexistují, import ze Stravy je v produkci vypnutý, dokud je majitel nepřidá.
- **Zastavovací body přechodu** (souhlas majitele): GHCR login a viditelnost balíčku, dodání druhého admina, nasazení do produkce, smazání volume/proměnných/secrets.

## Ponecháno na builderovi

- Rozdělení kódu do složek a tříd, jména služeb.
- Způsob konverze HEIC (Magick.NET), čtení EXIF orientace, kvalita JPEG v rozumných mezích.
- Implementace zjednodušení trasy za podmínky shody s dnešními výstupy.
- Detaily validace formulářů a chybových hlášek.
- Rozdělení implementace na úkoly pro Sonnet agenty bez překryvu souborů.
