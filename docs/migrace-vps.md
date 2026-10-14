# Baruch's Treks na sdíleném VPS: runbook

Aplikace běží na stejném OVH VPS jako `toiletlabels`, `manarozeniny` a `az-portal-python`, za jejich sdíleným Caddy,
na adrese <https://treks.austy.cz>. Postup je shodný s ToiletLabels (`ToiletLabelsPython/docs/migrace-vps.md`).

## Stav

Migrace z Azure Web App je dokončená (14. 10. 2026).

- **Data se nestěhovala.** Výlety jsou v Azure Table (`Trips`), fotky a GPX v Azure Blob (storage účet `baruchstreks`).
  VPS se připojuje přes `BARUCHSTREKS_STORAGE_CONNECTION`. Strava tokeny jsou v tabulce `Trips` (partition `Config`).
- **Databáze je SQLite ve volume `app_data` (`/data/db.sqlite3`).** Obsahuje jen uživatelské účty a sessions. Bez
  přenosu databáze z Azure existuje jen superuživatel z `DJANGO_SUPERUSER_*`. Heslo další uživatele nebo změnu hesla
  řeší příkaz níže.
- **Nasazení:** každý push do `main` spustí `.github/workflows/deploy-vps.yml` (testy, build image do GHCR
  `ghcr.io/austy246/baruchstreks-app`, deploy přes SSH, reload sdíleného Caddy). Jde spustit i ručně (Actions → Deploy to VPS).
- **Azure:** Web App `b-treks` je smazaná, workflow `azure-webapps-deploy.yml` také. Zůstává storage účet `baruchstreks`
  a Cosmos DB účet `baruchs-treks` ve skupině `BaruchsTreks`. Prázdný App Service Plan `b-treks-plan` (Free) lze smazat:
  `az appservice plan delete -n b-treks-plan -g BaruchsTreks --subscription b355f86c-94b4-467b-b643-1206cbf0e24c --yes`.

## Server

- Adresář `/srv/baruchstreks` (vlastník `deploy`), v něm repo a `deploy/` s `docker-compose.yml` a `.env`
  (vygeneruje se při prvním deployi, nepřepisuje se).
- Caddy blok: `/srv/infra/sites/baruchstreks.caddy` (z `deploy/baruchstreks.caddy`, doména se doplní z `ALLOWED_HOSTS`).
- Kontejner `baruchstreks-app-1`, v síti `web` pod aliasem `baruchstreks-app`.
- Přístup: `ssh -i ~/.ssh/id_rsa_austy deploy@57.131.165.173` (skupina `docker`, bez sudo); `debian@` má sudo.
- CI se přihlašuje vlastním klíčem `baruchstreks-ci` (secret `SSH_KEY`, veřejná část v `~deploy/.ssh/authorized_keys`;
  odvolání = smazat ten řádek). Klíč musí být bez passphrase.

## DNS

Zóna `austy.cz` (WEDOS) má wildcard `*` A i AAAA na jiný stroj, proto musí mít `treks` vlastní **A** záznam
`57.131.165.173` i **AAAA** záznam `2001:41d0:701:1100::ea6c`. Bez AAAA by Let's Encrypt při ověření skončil
na wildcardu a Caddy by nezískal certifikát. Změny v panelu je potřeba potvrdit tlačítkem „aplikovat změny“.
Ověření: `nslookup -type=AAAA treks.austy.cz ns.wedos.cz`.

## GitHub secrets (repo `austy246/BaruchsTreksV2`)

`SSH_HOST`, `SSH_USER`, `SSH_KEY`, `APP_DOMAIN` (`treks.austy.cz`), `BARUCHSTREKS_STORAGE_CONNECTION`,
`MAPY_CZ_API_KEY`, `DJANGO_SUPERUSER_USERNAME`, `DJANGO_SUPERUSER_PASSWORD`; volitelně `STRAVA_CLIENT_ID` /
`STRAVA_CLIENT_SECRET`. Prázdný secret se na serveru nepřepíše. Nepoužívané zbytky po Azure (`AZURE_CLIENT_ID`,
`AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `SECRET_KEY`) lze smazat, stejně jako federated credential aplikace
`baruchstreks-github` v Entra ID.

## Provoz

- Stav: `cd /srv/baruchstreks/deploy && docker compose ps` (služba `app` má být `healthy`), logy `docker compose logs -f app`.
- Zdraví: `curl -I https://treks.austy.cz/healthz` → 200.
- Změna hesla uživatele (`createsuperuser` nebo `changepassword`):
  ```bash
  ssh -t -i ~/.ssh/id_rsa_austy deploy@57.131.165.173 "docker exec -it baruchstreks-app-1 python manage.py changepassword <uživatel>"
  ```
  Změna secretu `DJANGO_SUPERUSER_PASSWORD` heslo existujícího uživatele nemění, účet se vytváří jen poprvé.
- Strava: *Authorization Callback Domain* v aplikaci na strava.com/settings/api musí být `treks.austy.cz`.
- Lokální zkouška image: `docker build -t baruchstreks-app:local .` a
  `docker run --rm -p 8000:8000 -e ALLOWED_HOSTS=localhost baruchstreks-app:local`, pak `curl localhost:8000/healthz`.

## Odstranění z VPS

`rm /srv/infra/sites/baruchstreks.caddy` + `caddy reload` v Caddy kontejneru (projekt `deploy`) a `docker compose down`
v `/srv/baruchstreks/deploy` (volume `baruchstreks_app_data` smazat jen záměrně, drží účty).
