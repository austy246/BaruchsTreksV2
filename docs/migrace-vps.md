# Migrace Baruch's Treks na sdílený VPS: runbook

Cíl: aplikace běží na stejném OVH VPS jako `toiletlabels`, `manarozeniny` a `az-portal-python`, za jejich sdíleným Caddy.
Postup je shodný s ToiletLabels (`ToiletLabelsPython/docs/migrace-vps.md`).

## Co se mění a co ne

- **Data se nestěhují.** Výlety jsou v Azure Table (`Trips`), fotky a GPX v Azure Blob. VPS se připojí ke stejnému
  storage účtu přes `BARUCHSTREKS_STORAGE_CONNECTION`. Strava tokeny jsou v tabulce `Trips` (partition `Config`),
  přenesou se tedy samy.
- **Databáze je SQLite ve volume `app_data` (`/data/db.sqlite3`).** Obsahuje jen účet superuživatele a sessions.
  Superuživatele vytvoří `docker-entrypoint.sh` z `DJANGO_SUPERUSER_*`. Po přepnutí se musíte jednou znovu přihlásit.
- Azure Web App (`azure-webapps-deploy.yml`) dál nasazuje při pushi do `main`, dokud nepřepnete provoz.
  VPS workflow (`deploy-vps.yml`) je zatím jen ruční.

## Stav

- **Hotovo:** Dockerfile, `docker-entrypoint.sh`, `deploy/`, workflow `deploy-vps.yml`, `/healthz`, proměnné
  `DJANGO_DB_PATH` a `CSRF_TRUSTED_ORIGINS` v `settings.py`. Image se lokálně sestaví, testy (`manage.py test trips`)
  v něm projdou a `/healthz` vrací 200.
- **GitHub secrets (repo `austy246/BaruchsTreksV2`):** `SSH_HOST`, `SSH_USER`, `SSH_KEY` (vlastní klíč projektu
  `baruchstreks-ci`), `DJANGO_SUPERUSER_USERNAME`, `DJANGO_SUPERUSER_PASSWORD`. Znovu se použijí stávající
  `BARUCHSTREKS_STORAGE_CONNECTION` a `MAPY_CZ_API_KEY`. Volitelně `STRAVA_CLIENT_ID` / `STRAVA_CLIENT_SECRET`
  (v Azure App Service nejsou nastaveny, takže se zatím nepřenášejí).
- **Zbývá:**
  1. Doména `treks.austy.cz` (secret `APP_DOMAIN` hotový).
  2. Na serveru přidat veřejnou část klíče `baruchstreks-ci` do `~deploy/.ssh/authorized_keys` a vytvořit adresář
     `/srv/baruchstreks` (příkazy níže).
  3. DNS A záznam domény na `57.131.165.173`.
  4. Spustit **Deploy to VPS** a ověřit krok B.

## Prerekvizity na serveru (už hotové díky ostatním projektům)

Síť `web`, adresář `/srv/infra/sites` (vlastník `deploy`) a sdílený Caddy s `import /etc/caddy/sites.d/*.caddy`.
Ověřit: `docker network ls | grep web` a `ls -ld /srv/infra/sites`.

Jednou, jako `debian` (má sudo):

```bash
sudo mkdir -p /srv/baruchstreks && sudo chown deploy:deploy /srv/baruchstreks
```

Veřejný klíč pro CI přidat do `authorized_keys` uživatele `deploy` (řádek lze později smazat = odvolání přístupu).
Přístup na server: `ssh -i ~/.ssh/id_rsa_austy deploy@57.131.165.173`.

## A. Příprava

1. **Doména:** `treks.austy.cz` (secret `APP_DOMAIN` je nastaven):
   ```powershell
   gh secret set APP_DOMAIN -R austy246/BaruchsTreksV2 --body "treks.austy.cz"
   ```
   V DNS přidat A záznam na `57.131.165.173` (TTL 300 s) **před prvním deployem**, jinak Caddy opakovaně selhává
   při HTTP-01. Ověřit `nslookup <doména>`; AAAA záznam nesmí vracet wildcard jiného stroje.
2. Image se publikuje do GHCR jako `ghcr.io/austy246/baruchstreks-app`. Deploy se přihlašuje tokenem workflow.
3. Lokální zkouška image (volitelné): `docker build -t baruchstreks-app:local .` a
   `docker run --rm -p 8000:8000 -e ALLOWED_HOSTS=localhost baruchstreks-app:local`, pak `curl localhost:8000/healthz`.

## B. První nasazení (bez dopadu na uživatele)

1. Actions → **Deploy to VPS** → Run workflow.
2. Na serveru: `cd /srv/baruchstreks/deploy && docker compose ps` (služba `app` musí být `healthy`).
3. `curl -I https://treks.austy.cz/healthz` → 200. Přihlásit se na `/accounts/login/`, zkontrolovat seznam výletů, mapu
   (`MAPY_CZ_API_KEY`), nahrání fotky a GPX, editaci a (pokud je zapnutý) import ze Stravy.

## C. Přepnutí

1. **Strava:** v nastavení aplikace na strava.com/settings/api změnit *Authorization Callback Domain* na novou doménu.
2. Nahradit odkazy na `b-treks.azurewebsites.net` novou adresou.
3. Azure Web App nechat běžet, dokud se nová adresa neosvědčí (oba používají stejná data v Azure, takže je to bezpečné).

## D. Návrat

Nic se nemigrovalo, takže stačí dál používat Azure Web App. Odstranění z VPS:
`rm /srv/infra/sites/baruchstreks.caddy` + `caddy reload` v Caddy kontejneru a `docker compose down` v
`/srv/baruchstreks/deploy`.

## E. Po několika dnech

1. Přepnout `deploy-vps.yml` na `on: push: branches: [main]` a smazat `azure-webapps-deploy.yml`.
2. Vypnout Azure Web App `b-treks` a smazat nepotřebné secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
   `AZURE_SUBSCRIPTION_ID` včetně federated credential.
3. Aktualizovat `README.md` (sekce Deployment).
4. Zvážit rotaci klíče storage účtu.
