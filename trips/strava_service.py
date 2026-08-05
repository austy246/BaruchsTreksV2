"""Strava integration for importing activity tracks.

Garmin devices sync to Strava automatically, and unlike Garmin's own Connect
Developer Program - which is partner-approval only - Strava offers a self-serve
OAuth API. So Strava is used as the bridge: the activity recorded on the Garmin
watch is pulled from Strava as coordinate streams and rebuilt into a GPX file,
the same format a manual Garmin Connect export produces.

Only the connecting user's own activities are ever read.
"""

import logging
import time
from datetime import datetime, timedelta, timezone

import requests
from django.conf import settings

from .gpx_service import build_gpx

logger = logging.getLogger(__name__)

REQUEST_TIMEOUT = 30


class StravaNotConfigured(Exception):
    """Raised when STRAVA_CLIENT_ID / STRAVA_CLIENT_SECRET are missing."""


class StravaNotConnected(Exception):
    """Raised when the user has no stored Strava tokens."""


class StravaError(Exception):
    """Raised when the Strava API returns an error."""


class StravaService:
    """Thin wrapper around the Strava v3 API plus token storage."""

    AUTHORIZE_URL = 'https://www.strava.com/oauth/authorize'
    TOKEN_URL = 'https://www.strava.com/oauth/token'
    API_BASE = 'https://www.strava.com/api/v3'

    # activity:read_all also covers activities the athlete marked as private.
    SCOPE = 'activity:read_all'

    # Refresh a little before expiry so a long request cannot race the deadline.
    REFRESH_MARGIN_SECONDS = 300

    def __init__(self, table_service, client_id=None, client_secret=None):
        self.table_service = table_service
        self.client_id = client_id or getattr(settings, 'STRAVA_CLIENT_ID', '')
        self.client_secret = client_secret or getattr(settings, 'STRAVA_CLIENT_SECRET', '')

    @property
    def is_configured(self):
        return bool(self.client_id and self.client_secret)

    def _require_configured(self):
        if not self.is_configured:
            raise StravaNotConfigured(
                "Strava není nakonfigurovaná - nastav STRAVA_CLIENT_ID a STRAVA_CLIENT_SECRET."
            )

    # ------------------------------------------------------------------
    # OAuth
    # ------------------------------------------------------------------

    def authorize_url(self, redirect_uri, state):
        """Build the URL the user is sent to in order to grant access."""
        self._require_configured()
        from urllib.parse import urlencode

        params = {
            'client_id': self.client_id,
            'redirect_uri': redirect_uri,
            'response_type': 'code',
            'approval_prompt': 'auto',
            'scope': self.SCOPE,
            'state': state,
        }
        return f"{self.AUTHORIZE_URL}?{urlencode(params)}"

    def exchange_code(self, code):
        """Trade an authorization code for access and refresh tokens."""
        self._require_configured()

        response = requests.post(
            self.TOKEN_URL,
            data={
                'client_id': self.client_id,
                'client_secret': self.client_secret,
                'code': code,
                'grant_type': 'authorization_code',
            },
            timeout=REQUEST_TIMEOUT,
        )
        if response.status_code != 200:
            raise StravaError(f"Výměna kódu za token selhala ({response.status_code}): {response.text}")
        return response.json()

    def _refresh_tokens(self, tokens):
        """Exchange a refresh token for a fresh access token."""
        self._require_configured()

        response = requests.post(
            self.TOKEN_URL,
            data={
                'client_id': self.client_id,
                'client_secret': self.client_secret,
                'refresh_token': tokens.get('refresh_token'),
                'grant_type': 'refresh_token',
            },
            timeout=REQUEST_TIMEOUT,
        )
        if response.status_code != 200:
            raise StravaError(f"Obnovení tokenu selhalo ({response.status_code}): {response.text}")

        refreshed = response.json()
        # Strava returns only the token fields, so keep the athlete details.
        merged = dict(tokens)
        merged.update(refreshed)
        return merged

    # ------------------------------------------------------------------
    # Token storage
    # ------------------------------------------------------------------

    @staticmethod
    def _token_key(username):
        return f"strava_tokens_{username}"

    def save_tokens(self, username, tokens):
        return self.table_service.save_config(self._token_key(username), tokens)

    def get_tokens(self, username):
        return self.table_service.get_config(self._token_key(username))

    def is_connected(self, username):
        return self.get_tokens(username) is not None

    def disconnect(self, username):
        return self.table_service.delete_config(self._token_key(username))

    def get_access_token(self, username):
        """Return a valid access token, refreshing and re-storing when stale."""
        tokens = self.get_tokens(username)
        if not tokens:
            raise StravaNotConnected("Účet Strava není propojený.")

        expires_at = tokens.get('expires_at', 0)
        if expires_at - time.time() < self.REFRESH_MARGIN_SECONDS:
            logger.info(f"Refreshing Strava token for {username}")
            tokens = self._refresh_tokens(tokens)
            self.save_tokens(username, tokens)

        return tokens['access_token']

    # ------------------------------------------------------------------
    # API calls
    # ------------------------------------------------------------------

    def _get(self, username, path, params=None):
        access_token = self.get_access_token(username)
        response = requests.get(
            f"{self.API_BASE}{path}",
            headers={'Authorization': f"Bearer {access_token}"},
            params=params or {},
            timeout=REQUEST_TIMEOUT,
        )
        if response.status_code == 401:
            raise StravaNotConnected("Přístup k účtu Strava vypršel, propoj ho prosím znovu.")
        if response.status_code == 429:
            raise StravaError("Překročen limit požadavků na Stravu, zkus to prosím za chvíli.")
        if response.status_code != 200:
            raise StravaError(f"Strava vrátila chybu {response.status_code}: {response.text}")
        return response.json()

    def list_activities(self, username, page=1, per_page=30):
        """List the athlete's own activities, newest first."""
        activities = self._get(
            username, '/athlete/activities', {'page': page, 'per_page': per_page}
        )
        return [self._summarize_activity(activity) for activity in activities]

    def get_activity(self, username, activity_id):
        return self._summarize_activity(self._get(username, f'/activities/{activity_id}'))

    @staticmethod
    def _summarize_activity(activity):
        """Reduce a Strava activity to the fields the UI and importer need."""
        start_date_local = activity.get('start_date_local')
        start_date = None
        if start_date_local:
            try:
                start_date = datetime.fromisoformat(start_date_local.replace('Z', '+00:00')).date()
            except ValueError:
                start_date = None

        elapsed = activity.get('elapsed_time') or 0

        return {
            'id': activity.get('id'),
            'name': activity.get('name', ''),
            'sport_type': activity.get('sport_type') or activity.get('type', ''),
            'start_date': start_date,
            'start_date_local': start_date_local,
            'distance_km': round((activity.get('distance') or 0) / 1000.0, 2),
            'elevation_gain': int(round(activity.get('total_elevation_gain') or 0)),
            'elapsed_hours': round(elapsed / 3600.0, 2),
            'has_gps': bool(activity.get('start_latlng')),
        }

    def activity_gpx(self, username, activity_id):
        """Download an activity's streams and rebuild them as a GPX document.

        Returns:
            tuple: (gpx_bytes, activity summary dict)

        Raises:
            StravaError: the activity has no GPS data.
        """
        activity = self.get_activity(username, activity_id)

        streams = self._get(
            username,
            f'/activities/{activity_id}/streams',
            {'keys': 'latlng,altitude,time', 'key_by_type': 'true'},
        )

        latlng = (streams.get('latlng') or {}).get('data') or []
        if not latlng:
            raise StravaError("Aktivita neobsahuje GPS data, nelze z ní vytvořit trasu.")

        altitude = (streams.get('altitude') or {}).get('data') or []
        time_offsets = (streams.get('time') or {}).get('data') or []

        start_time = None
        if activity.get('start_date_local'):
            try:
                start_time = datetime.fromisoformat(
                    activity['start_date_local'].replace('Z', '+00:00')
                )
                if start_time.tzinfo is None:
                    start_time = start_time.replace(tzinfo=timezone.utc)
            except ValueError:
                start_time = None

        points = []
        for index, (lat, lng) in enumerate(latlng):
            elevation = altitude[index] if index < len(altitude) else None
            point_time = None
            if start_time and index < len(time_offsets):
                point_time = start_time + timedelta(seconds=time_offsets[index])
            points.append((lat, lng, elevation, point_time))

        logger.info(f"Built GPX from Strava activity {activity_id} with {len(points)} points")
        return build_gpx(activity['name'] or f"Strava {activity_id}", points, start_time), activity
