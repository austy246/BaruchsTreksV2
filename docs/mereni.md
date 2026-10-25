# Měření: Django vs. .NET

Stejné metriky před a po přechodu (spec, sekce Přechod v produkci, kroky 1 a 7).

## Django baseline

Produkce (VPS, image `ghcr.io/austy246/baruchstreks-app:6b91632…`, Django + gunicorn 2 workery × 4 vlákna, sqlite), naměřeno 25. 10. 2026
přímo na serveru (`curl https://treks.austy.cz`, 20 požadavků na cestu, sekundy). Kontejner běžel 8 dní, `mem_limit` 512 MB.

| Metrika | Hodnota | Jak |
|---|---|---|
| Velikost image | 292 MB (rozbalený) | `docker image ls` |
| Paměť kontejneru v klidu | 110,4 MiB (po měření 117,4 MiB) | `docker stats --no-stream` |
| CPU v klidu | 1,1 % (po měření 2,7 %) | `docker stats --no-stream` |
| `/healthz` | min 0,024 / medián 0,034 / p90 0,062 / max 0,741 | 20× curl |
| `/trips/` (přehled, 6 výletů + první fotky) | min 1,117 / medián 1,551 / p90 5,419 / max 6,991 | 20× curl |
| `/trips/all/` (seznam všech výletů) | min 0,275 / medián 0,305 / p90 2,856 / max 5,577 | 20× curl |

Odezva `/trips/` je dlouhá, protože Django pro každý výlet volá Azure Blob (výpis fotek) a nic necachuje. Stejné dotazy se musí zopakovat
po nasazení .NET verze (krok 7).

## Nová verze (.NET) – lokální měření

Lokální Docker Desktop (Windows, WSL2), `docker compose up -d --wait`, prázdný Azurite, `ASPNETCORE_ENVIRONMENT=Development`,
bez omezení paměti. Naměřeno 25. 10. 2026 po startu, po 20 s klidu a 10 dotazech na `/trips/`.

| Metrika | Hodnota | Jak |
|---|---|---|
| Velikost image (obsah, komprimovaně) | 91,8 MB (91 849 319 B) | `docker image inspect baruchstreks-app:local --format '{{.Size}}'` |
| Velikost image (rozbalený na disku) | 306 MB | `docker image ls baruchstreks-app:local` |
| Paměť kontejneru `baruchstreks-app-1` v klidu | 49,8 až 50,0 MiB | `docker stats --no-stream` |
| CPU v klidu | 0,5 % | `docker stats --no-stream` |

Image: `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra` (base pinnutý digestem), Workstation GC bez konkurentního GC.
Součástí image je i vzorek `SelfTest/sample.heic` (1,2 MB) pro `--selftest`.

Poznámka: lokální měření je orientační (prázdná data, žádné fotky ani trasy); srovnání s Django baseline a produkční
hodnoty se doplní po nasazení (spec, krok 7).

### Poznámky z end-to-end ověření

- Paměť aplikace v klidu: 105 MiB (`docker stats --no-stream`).
- Po nahrání jednoho 21 MB GPX (soubor se celý drží v paměti; limit je 25 MB GPX / 50 MB požadavek) vzrostla špička asi na 434 MiB
  a poté zůstala kolem 430 MiB (GC/LOH nevrací paměť).
- Relevantní pro limit 512 MB: maximální povolený požadavek se vejde jen s malou rezervou, proto limit v produkčním compose ověřit.
- Zdroj: `.superpowers/sdd/2026-10-25-dotnet-rewrite/e2e-report.md`.

## Produkce po přechodu (.NET, naměřeno 25. 10. 2026, hned po nasazení)

Stejný postup jako u Django baseline: přímo na VPS, `curl https://treks.austy.cz`, 20 požadavků na cestu, sekundy. Image
`ghcr.io/austy246/baruchstreks-app:f460e56…`, `mem_limit` 512 MB, produkční data (62 výletů).

| Metrika | Django | .NET | Změna |
|---|---|---|---|
| Paměť kontejneru v klidu | 110,4 MiB | 54,9 MiB (po měření 61,1 MiB) | zhruba polovina |
| `/healthz` medián | 0,034 s | 0,020 s | |
| `/trips/` medián / p90 / max | 1,551 / 5,419 / 6,991 s | 0,151 / 0,200 / 1,450 s | 10× rychlejší |
| `/trips/all/` medián / p90 / max | 0,305 / 2,856 / 5,577 s | 0,093 / 0,107 / 0,273 s | 3× rychlejší |
| Velikost image (rozbalený, `docker image ls` na VPS) | 292 MB | 306 MB | o 14 MB větší |

Poznámky:

- Velikost image je jediná metrika, která se nezlepšila: rozbalený image je o 14 MB větší (chiseled-extra s ICU a nativní Magick.NET
  s HEIC). Komprimovaný push do GHCR má 91,8 MB. Spec uvádí, že image nemá být těžší; s ohledem na poloviční paměť a řádově
  rychlejší odezvu to bere jako vědomý kompromis (rozhodnutí majitele je vhodné potvrdit).
- Rychlost přehledu je dána hlavně tím, že .NET výpisy fotek nezřetězuje do pomalých sekvenčních volání jako Django.
- Odezvy `/trips/` a `/trips/all/` Djanga měřené na serveru byly velmi rozkolísané (p90 až 5 s), .NET verze je stabilní.
