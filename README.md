# Baruch's Treks

Second version of Baruch's Treks which is based on Azure Storage and Django.

## Features

- Trip management with detailed information
- Interactive maps using Mapy.cz API
- Photo uploads for trips
- GPX track uploads, drawn on the trip map (see [GPX tracks](#gpx-tracks))
- Optional Strava import for tracks recorded on a Garmin device
- Start and finish point markers on maps
- Azure Table Storage for trip data
- Azure Blob Storage for trip photos and GPX tracks

## Deployment (VPS)

The app runs in a Docker container on a shared OVH VPS behind the shared Caddy, at
<https://treks.austy.cz>. A push to `main` runs `.github/workflows/deploy-vps.yml`: tests, image build and push to
GHCR (`ghcr.io/austy246/baruchstreks-app`), then a deploy over SSH. Full runbook, server layout and rollback:
[docs/migrace-vps.md](docs/migrace-vps.md).

- Data stays in Azure: trips in Table Storage (`Trips`), photos and GPX tracks in Blob Storage.
- SQLite (admin accounts and sessions) lives in the `app_data` volume (`/data/db.sqlite3`).
- `/healthz` is the health check used by Docker.

### GitHub secrets

`SSH_HOST`, `SSH_USER`, `SSH_KEY`, `APP_DOMAIN`, `BARUCHSTREKS_STORAGE_CONNECTION`, `MAPY_CZ_API_KEY`,
`DJANGO_SUPERUSER_USERNAME`, `DJANGO_SUPERUSER_PASSWORD`; optional `STRAVA_CLIENT_ID` / `STRAVA_CLIENT_SECRET`.
Server-side settings (`SECRET_KEY`, `ALLOWED_HOSTS`, `CSRF_TRUSTED_ORIGINS`) are generated into `deploy/.env` on the
server on the first deploy.

The former Azure Web App (`b-treks`) was deleted after the migration. The Azure Storage account `baruchstreks` is still
used.

### Dependency versions

`requirements.txt` caps every dependency below the next major version. The Docker image resolves dependencies fresh
at build time, so an uncapped requirement installs whatever major version is current — which is not what the code was
tested against. (On the former Azure App Service this once pulled in Django 6.1, which refused to start because it needs
SQLite 3.37+.)

Raise a cap deliberately, together with running the tests against the new version.

### Local Development

1. Clone the repository
2. Create a virtual environment: `python -m venv venv`
3. Activate the virtual environment:
   - Windows: `venv\Scripts\activate`
   - Unix/MacOS: `source venv/bin/activate`
4. Install dependencies: `pip install -r requirements.txt`
5. Create a `.env` file with the required environment variables (see `.env.example`)
6. Run the development server: `python manage.py runserver`
7. Run the tests: `python manage.py test trips`

## GPX tracks

A trip can carry one GPX track, shown as a line on the trip map with the
original file offered for download.

Below the map sits an elevation profile. Moving along it — with the mouse or by
dragging a finger — marks the matching spot on the map and shows the distance
and elevation at that point, the way Garmin Connect does. On touch the mark
stays after the finger lifts and clears on the next tap elsewhere. The chart is
plain inline SVG drawn from the track's own coordinates, so it needs no charting
library and no extra request.

### Uploading a track

In the trip edit form, section **Trasa (GPX)**. To get the file out of a Garmin
watch, open the activity in Garmin Connect and choose ⚙ → *Export to GPX*
(activities recorded as courses export as `<rte>` instead of `<trk>`; both are
accepted).

A GPX can supply six trip fields: elevation gain, elevation loss, duration,
completion date, start point and high point. What happens to them depends on
whether the trip already has them:

- **Blank fields are filled in straight away.** Nothing is lost, so nothing is
  asked. The upload lands back in the editor with the values in place and the
  track drawn on the map, ready to be adjusted before saving again.
- **Fields that already hold something are never overwritten silently.** The
  upload goes to a confirmation page listing each one with its current value
  beside the value from the GPX, pre-ticked, and only the ticked ones are
  written. A value that already matches the GPX is not a conflict and is not
  listed.

The track and its statistics are stored either way — the confirmation decides
only what happens to the trip's own fields.

This matters most for the high point, which is simply the highest coordinate in
the file. That is usually the summit the trip was about, but not always, so
after taking it from the GPX the marker can be dragged along the track on the
editing map and saved.

### Importing from Strava

Garmin devices sync to Strava automatically, and unlike Garmin's own Connect
Developer Program — which is partner-approval only, for business use — Strava
has a self-serve API. So Strava is used as the bridge: **Import trasy** in the
navigation lists the connected athlete's activities and rebuilds the chosen one
into a GPX file. It is optional; without credentials the page explains the setup
and manual upload keeps working.

1. Create an app at [strava.com/settings/api](https://www.strava.com/settings/api).
2. Set *Authorization Callback Domain* to the site's domain, `treks.austy.cz` (for local
   development, `localhost`).
3. Set `STRAVA_CLIENT_ID` and `STRAVA_CLIENT_SECRET` — as environment variables
   locally, or as GitHub secrets for the VPS deploy.

Only the connecting user's own activities are read. OAuth tokens are stored in
the `Trips` table under the `Config` partition, so they never show up among the
trips, and can be revoked from the Strava page with *Odpojit Stravu*.

### How a track is stored

Azure Table Storage caps a string property at 64 KB, well below the size of a
typical Garmin track, so only statistics land in the table (`TrackJson`) while
the files go to Blob Storage, in a `tracks` container created on first upload:

- `{trip_id}/track.gpx` — the original file, untouched
- `{trip_id}/track.geojson` — the track simplified for the map

Simplification is Ramer–Douglas–Peucker with a 5 m tolerance, capped at 2000
points, which keeps a multi-hour track well under a hundred kilobytes. Elevation
gain ignores changes below 3 m so GPS jitter does not inflate a flat walk.

Both files are served through Django rather than by blob URL, so the container
needs neither public read access nor a CORS rule.

## Security

- All sensitive information is stored in environment variables
- The `.gitignore` file is configured to prevent committing sensitive files
- Production deployment uses HTTPS with security headers
