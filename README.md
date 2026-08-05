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

## Deployment to Azure Web App

This project is configured for continuous deployment to Azure Web App using GitHub Actions.

### Prerequisites

1. An Azure account with an active subscription
2. An Azure Web App named "baruchstreks"
3. Azure Storage Account named "baruchstreks" with:
   - A table named "Trips"
   - A blob container for storing photos

### Setting up Continuous Deployment

1. **Create the Azure Web App**:
   - Go to the Azure Portal and create a new Web App named "b-treks"
   - Select Python 3.12 as the runtime stack
   - Configure the app to use Linux

2. **Create Azure Credentials**:
   - Open Azure Cloud Shell (bash) from the Azure Portal
   - Run the following command to create a service principal with Contributor role:
     ```bash
     az ad sp create-for-rbac --name "baruchstreks-github" --role contributor \
       --scopes /subscriptions/b355f86c-94b4-467b-b643-1206cbf0e24c/resourceGroups/BaruchsTreks/providers/Microsoft.Web/sites/baruchstreks
     ```
   - Replace `{subscription-id}` with your Azure subscription ID and `{resource-group}` with your resource group name
   - The command will output JSON similar to this:
     ```json
     {
       "appId": "example-app-id",
       "displayName": "baruchstreks-github",
       "password": "example-password",
       "tenant": "example-tenant-id"
     }
     ```
   - You need to transform this output into the format required by the GitHub Actions Azure login:
     ```json
     {
       "clientId": "THE-APP-ID-FROM-ABOVE",
       "clientSecret": "THE-PASSWORD-FROM-ABOVE",
       "tenantId": "THE-TENANT-FROM-ABOVE",
       "subscriptionId": "YOUR-AZURE-SUBSCRIPTION-ID"
     }
     ```

3. **Configure GitHub Secrets**:
   - In your GitHub repository, go to Settings > Secrets and Variables > Actions
   - Add the following secrets:
     - `AZURE_CREDENTIALS`: The properly formatted JSON from step 2
     - `BARUCHSTREKS_STORAGE_CONNECTION`: Your Azure Storage connection string
     - `MAPY_CZ_API_KEY`: Your Mapy.cz API key
     - `SECRET_KEY`: A secure Django secret key

4. **Configure App Settings in Azure**:
   - In the Azure Portal, go to your Web App > Configuration > Application settings
   - Add the following settings:
     - `BARUCHSTREKS_STORAGE_CONNECTION`: Your Azure Storage connection string
     - `MAPY_CZ_API_KEY`: Your Mapy.cz API key
     - `STRAVA_CLIENT_ID` / `STRAVA_CLIENT_SECRET`: Optional, enables the Strava track import
     - `DEBUG`: Set to "False"
     - `SECRET_KEY`: A secure Django secret key
     - `ALLOWED_HOSTS`: "b-treks.azurewebsites.net"

5. **Push to Main Branch**:
   - When you push to the main branch, the GitHub Actions workflow will automatically:
     - Build the application
     - Collect static files
     - Deploy to Azure Web App

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

### Uploading a track

In the trip edit form, section **Trasa (GPX)**. To get the file out of a Garmin
watch, open the activity in Garmin Connect and choose ⚙ → *Export to GPX*
(activities recorded as courses export as `<rte>` instead of `<trk>`; both are
accepted).

With **autofill** ticked, the values derived from the GPX replace the elevation
gain, elevation loss, duration and the start/high point of the trip. A
completion date that is already filled in is left alone.

### Importing from Strava

Garmin devices sync to Strava automatically, and unlike Garmin's own Connect
Developer Program — which is partner-approval only, for business use — Strava
has a self-serve API. So Strava is used as the bridge: **Import trasy** in the
navigation lists the connected athlete's activities and rebuilds the chosen one
into a GPX file. It is optional; without credentials the page explains the setup
and manual upload keeps working.

1. Create an app at [strava.com/settings/api](https://www.strava.com/settings/api).
2. Set *Authorization Callback Domain* to the site's domain (for local
   development, `localhost`).
3. Set `STRAVA_CLIENT_ID` and `STRAVA_CLIENT_SECRET` — as environment variables
   locally, or as App Settings on the Azure Web App.

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
