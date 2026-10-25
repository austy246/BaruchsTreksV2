# Měření: Django vs. .NET

Stejné metriky před a po přechodu (spec, sekce Přechod v produkci, kroky 1 a 7).

## Django baseline

_Doplní řídicí agent před nasazením (paměť kontejneru z `docker stats`, odezva 20× `/trips/` a `/healthz`, velikost image)._

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
