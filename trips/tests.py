import json
from datetime import datetime, timedelta, timezone
from unittest import mock

from django.contrib.auth.models import User
from django.core.files.uploadedfile import SimpleUploadedFile
from django.test import SimpleTestCase, TestCase
from django.urls import reverse

from .gpx_service import GpxParseError, build_gpx, geojson_bytes, parse_gpx
from .strava_service import StravaError, StravaService


def make_gpx(points, tag='trk'):
    """Build a minimal GPX document from (lat, lng, ele, seconds_offset) tuples."""
    start = datetime(2025, 7, 12, 6, 0, 0, tzinfo=timezone.utc)
    point_tag = 'trkpt' if tag == 'trk' else 'rtept'

    body = []
    for lat, lng, elevation, offset in points:
        time = (start + timedelta(seconds=offset)).strftime('%Y-%m-%dT%H:%M:%SZ')
        body.append(
            f'<{point_tag} lat="{lat}" lon="{lng}">'
            f'<ele>{elevation}</ele><time>{time}</time>'
            f'</{point_tag}>'
        )

    inner = ''.join(body)
    if tag == 'trk':
        content = f'<trk><name>Test</name><trkseg>{inner}</trkseg></trk>'
    else:
        content = f'<rte><name>Test</name>{inner}</rte>'

    return (
        '<?xml version="1.0" encoding="UTF-8"?>'
        '<gpx version="1.1" creator="test" xmlns="http://www.topografix.com/GPX/1/1">'
        f'{content}</gpx>'
    ).encode('utf-8')


class ParseGpxTests(SimpleTestCase):
    def test_parses_track_statistics(self):
        # Roughly 111 m per 0.001 degree of latitude, climbing then descending.
        gpx = make_gpx([
            (50.000, 14.000, 400, 0),
            (50.001, 14.000, 500, 600),
            (50.002, 14.000, 700, 1200),
            (50.003, 14.000, 450, 1800),
        ])

        parsed = parse_gpx(gpx)
        stats = parsed['stats']

        # Ground distance, so the 100-250 m of climbing does not stretch it.
        self.assertAlmostEqual(stats['distance_km'], 0.33, delta=0.02)
        self.assertEqual(stats['meters_ascend'], 300)
        self.assertEqual(stats['meters_descend'], 250)
        self.assertEqual(stats['duration_hours'], 0.5)
        self.assertEqual(stats['max_elevation'], 700)
        self.assertEqual(stats['min_elevation'], 400)

    def test_start_and_high_point(self):
        gpx = make_gpx([
            (50.000, 14.000, 400, 0),
            (50.001, 14.000, 900, 600),
            (50.002, 14.000, 500, 1200),
        ])

        parsed = parse_gpx(gpx)

        self.assertEqual(parsed['start'], {'Latitude': 50.0, 'Longtitude': 14.0})
        self.assertEqual(parsed['high_point']['Latitude'], 50.001)
        self.assertEqual(parsed['high_point']['Elevation'], 900)
        self.assertEqual(parsed['started_at'].date().isoformat(), '2025-07-12')

    def test_elevation_noise_below_threshold_is_ignored(self):
        # A flat walk whose recorded elevation jitters by a meter each fix.
        points = []
        for i in range(40):
            points.append((50.0 + i * 0.0001, 14.0, 400 + (i % 2), i * 10))

        stats = parse_gpx(make_gpx(points))['stats']

        self.assertEqual(stats['meters_ascend'], 0)
        self.assertEqual(stats['meters_descend'], 0)

    def test_simplification_drops_collinear_points(self):
        # A dead straight line needs only its two endpoints.
        points = [(50.0 + i * 0.0005, 14.0, 400, i * 10) for i in range(50)]

        parsed = parse_gpx(make_gpx(points))

        self.assertEqual(parsed['stats']['original_point_count'], 50)
        self.assertEqual(parsed['stats']['point_count'], 2)

    def test_simplification_keeps_the_shape_of_a_turn(self):
        points = [
            (50.000, 14.000, 400, 0),
            (50.005, 14.000, 400, 600),
            (50.005, 14.010, 400, 1200),
            (50.010, 14.010, 400, 1800),
        ]

        parsed = parse_gpx(make_gpx(points))

        self.assertEqual(parsed['stats']['point_count'], 4)

    def test_routes_are_read_when_there_is_no_track(self):
        # Garmin exports planned courses as <rte> rather than <trk>.
        gpx = make_gpx([
            (50.000, 14.000, 400, 0),
            (50.002, 14.000, 600, 1200),
        ], tag='rte')

        stats = parse_gpx(gpx)['stats']

        self.assertEqual(stats['meters_ascend'], 200)
        self.assertGreater(stats['distance_km'], 0)

    def test_bounds_cover_every_point(self):
        parsed = parse_gpx(make_gpx([
            (50.000, 14.000, 400, 0),
            (50.010, 14.020, 500, 600),
            (49.990, 14.010, 450, 1200),
        ]))

        (min_lat, min_lng), (max_lat, max_lng) = parsed['stats']['bounds']

        self.assertEqual(min_lat, 49.99)
        self.assertEqual(max_lat, 50.01)
        self.assertEqual(min_lng, 14.0)
        self.assertEqual(max_lng, 14.02)

    def test_geojson_is_a_linestring_in_lng_lat_order(self):
        parsed = parse_gpx(make_gpx([
            (50.000, 14.000, 400, 0),
            (50.002, 14.005, 600, 1200),
        ]))

        feature = json.loads(geojson_bytes(parsed))
        coordinates = feature['geometry']['coordinates']

        self.assertEqual(feature['geometry']['type'], 'LineString')
        self.assertEqual(coordinates[0][:2], [14.0, 50.0])
        self.assertEqual(coordinates[0][2], 400)

    def test_missing_elevation_is_tolerated(self):
        gpx = (
            '<?xml version="1.0" encoding="UTF-8"?>'
            '<gpx version="1.1" creator="test" xmlns="http://www.topografix.com/GPX/1/1">'
            '<trk><trkseg>'
            '<trkpt lat="50.0" lon="14.0"></trkpt>'
            '<trkpt lat="50.002" lon="14.0"></trkpt>'
            '</trkseg></trk></gpx>'
        ).encode('utf-8')

        stats = parse_gpx(gpx)['stats']

        self.assertEqual(stats['meters_ascend'], 0)
        self.assertIsNone(stats['max_elevation'])
        self.assertIsNone(stats['duration_hours'])

    def test_non_gpx_content_is_rejected(self):
        with self.assertRaises(GpxParseError):
            parse_gpx(b'this is not a gpx file')

    def test_gpx_without_points_is_rejected(self):
        empty = (
            '<?xml version="1.0" encoding="UTF-8"?>'
            '<gpx version="1.1" creator="test" xmlns="http://www.topografix.com/GPX/1/1">'
            '</gpx>'
        ).encode('utf-8')

        with self.assertRaises(GpxParseError):
            parse_gpx(empty)


class BuildGpxTests(SimpleTestCase):
    def test_built_gpx_can_be_parsed_back(self):
        # This is the round trip a Strava import makes: streams -> GPX -> stats.
        start = datetime(2025, 7, 12, 6, 0, 0, tzinfo=timezone.utc)
        points = [
            (50.000, 14.000, 400, start),
            (50.001, 14.000, 500, start + timedelta(seconds=900)),
            (50.002, 14.000, 700, start + timedelta(seconds=1800)),
        ]

        parsed = parse_gpx(build_gpx('Ranní výběh', points))

        self.assertEqual(parsed['stats']['meters_ascend'], 300)
        self.assertEqual(parsed['stats']['duration_hours'], 0.5)
        self.assertEqual(parsed['start'], {'Latitude': 50.0, 'Longtitude': 14.0})

    def test_points_without_time_produce_no_duration(self):
        points = [(50.0, 14.0, 400, None), (50.002, 14.0, 600, None)]

        parsed = parse_gpx(build_gpx('No time', points))

        self.assertIsNone(parsed['stats']['duration_hours'])
        self.assertEqual(parsed['stats']['meters_ascend'], 200)


class StravaActivityGpxTests(SimpleTestCase):
    """The stream-to-GPX conversion, with the HTTP layer stubbed out."""

    ACTIVITY = {
        'id': 42,
        'name': 'Sněžka',
        'sport_type': 'Hike',
        'start_date_local': '2025-07-12T06:00:00Z',
        'distance': 12400.0,
        'total_elevation_gain': 980.0,
        'elapsed_time': 18900,
        'start_latlng': [50.0, 14.0],
    }

    STREAMS = {
        'latlng': {'data': [[50.0, 14.0], [50.001, 14.0], [50.002, 14.0]]},
        'altitude': {'data': [400.0, 500.0, 700.0]},
        'time': {'data': [0, 900, 1800]},
    }

    def build_service(self, streams=None):
        service = StravaService(table_service=mock.Mock(), client_id='id', client_secret='secret')

        def fake_get(username, path, params=None):
            if path.endswith('/streams'):
                return self.STREAMS if streams is None else streams
            return self.ACTIVITY

        service._get = fake_get
        return service

    def test_activity_summary_fields(self):
        activity = self.build_service().get_activity('baruch', 42)

        self.assertEqual(activity['name'], 'Sněžka')
        self.assertEqual(activity['distance_km'], 12.4)
        self.assertEqual(activity['elevation_gain'], 980)
        self.assertEqual(activity['elapsed_hours'], 5.25)
        self.assertEqual(activity['start_date'].isoformat(), '2025-07-12')
        self.assertTrue(activity['has_gps'])

    def test_streams_become_a_parsable_gpx(self):
        gpx_bytes, activity = self.build_service().activity_gpx('baruch', 42)
        parsed = parse_gpx(gpx_bytes)

        self.assertEqual(activity['id'], 42)
        self.assertEqual(parsed['stats']['meters_ascend'], 300)
        self.assertEqual(parsed['stats']['duration_hours'], 0.5)
        self.assertEqual(parsed['start'], {'Latitude': 50.0, 'Longtitude': 14.0})

    def test_activity_without_gps_is_rejected(self):
        service = self.build_service(streams={'latlng': {'data': []}})

        with self.assertRaises(StravaError):
            service.activity_gpx('baruch', 42)


class TripEditGpxUploadTests(TestCase):
    """The upload path end to end, with Azure Table and Blob storage mocked."""

    GPX = make_gpx([
        (50.000, 14.000, 400, 0),
        (50.001, 14.000, 900, 900),
        (50.002, 14.000, 500, 1800),
    ])

    def setUp(self):
        self.user = User.objects.create_user('baruch', password='x', is_staff=True)
        self.client.force_login(self.user)

        self.table = mock.Mock()
        self.table.create_trip.return_value = (True, None, 'trip_20250712060000')
        self.table.update_trip.return_value = (True, "")
        self.table.get_trip_by_id.return_value = None

        self.track_service = mock.Mock()
        self.track_service.upload_track.return_value = (True, "")

        patches = [
            mock.patch('trips.views.get_data_service', return_value=self.table),
            mock.patch('trips.views.AzureTrackService', return_value=self.track_service),
            mock.patch('trips.views.AzureBlobService'),
        ]
        for patch in patches:
            patch.start()
            self.addCleanup(patch.stop)

    def post_new_trip(self, gpx=None, **extra):
        data = {'title': 'Sněžka', 'description': '', 'participants': '',
                'parking_json': '', 'high_point_json': ''}
        data.update(extra)
        if gpx is not None:
            data['gpx_file'] = SimpleUploadedFile('activity.gpx', gpx, content_type='application/gpx+xml')
        return self.client.post(reverse('trips:trip_create'), data)

    def test_upload_fills_blank_fields_and_stores_the_track(self):
        response = self.post_new_trip(self.GPX)

        # Filled-in values are a starting point, so the editor stays open for
        # them to be corrected rather than jumping to the detail page.
        self.assertRedirects(
            response,
            reverse('trips:trip_edit', kwargs={'trip_id': 'trip_20250712060000'}),
            fetch_redirect_response=False,
        )

        trip_data = self.table.create_trip.call_args[0][0]
        self.assertEqual(trip_data['meters_ascend'], 500)
        self.assertEqual(trip_data['meters_descend'], 400)
        self.assertEqual(trip_data['length_hours'], 0.5)
        self.assertEqual(json.loads(trip_data['parking_json']),
                         {'Latitude': 50.0, 'Longtitude': 14.0})
        self.assertEqual(json.loads(trip_data['high_point_json'])['Latitude'], 50.001)
        # A blank completion date is taken from the GPX timestamps.
        self.assertEqual(trip_data['trip_completed_on'], '2025-07-12')

        stats = json.loads(trip_data['track_json'])
        self.assertEqual(stats['meters_ascend'], 500)
        # The stored record carries the points the form can be refilled from
        self.assertEqual(stats['start'], {'Latitude': 50.0, 'Longtitude': 14.0})
        self.assertEqual(stats['started_on'], '2025-07-12')

        trip_id, gpx_bytes, geojson = self.track_service.upload_track.call_args[0]
        self.assertEqual(trip_id, 'trip_20250712060000')
        self.assertEqual(gpx_bytes, self.GPX)
        self.assertEqual(json.loads(geojson)['geometry']['type'], 'LineString')

    def test_typed_values_are_never_overwritten_without_asking(self):
        response = self.post_new_trip(self.GPX, meters_ascend=111, meters_descend=222,
                                      length_hours=9, trip_completed_on='2024-01-02')

        # Everything the GPX would replace is left alone until confirmed
        self.assertRedirects(
            response,
            reverse('trips:trip_track_apply', kwargs={'trip_id': 'trip_20250712060000'}),
            fetch_redirect_response=False,
        )

        trip_data = self.table.create_trip.call_args[0][0]
        self.assertEqual(trip_data['meters_ascend'], 111)
        self.assertEqual(trip_data['meters_descend'], 222)
        self.assertEqual(trip_data['length_hours'], 9)
        self.assertEqual(trip_data['trip_completed_on'].isoformat(), '2024-01-02')
        # The blanks still get filled, and the track is stored either way
        self.assertEqual(json.loads(trip_data['parking_json']),
                         {'Latitude': 50.0, 'Longtitude': 14.0})
        self.assertTrue(trip_data['track_json'])
        self.track_service.upload_track.assert_called_once()

    def test_a_value_matching_the_gpx_is_not_treated_as_a_conflict(self):
        # 500 m of ascent is exactly what this GPX yields, so nothing to ask
        response = self.post_new_trip(self.GPX, meters_ascend=500)

        self.assertRedirects(
            response,
            reverse('trips:trip_edit', kwargs={'trip_id': 'trip_20250712060000'}),
            fetch_redirect_response=False,
        )

    def test_broken_gpx_is_reported_and_nothing_is_saved(self):
        response = self.post_new_trip(b'<gpx>not really</gpx>')

        self.assertEqual(response.status_code, 200)
        self.assertContains(response, 'GPX')
        self.table.create_trip.assert_not_called()
        self.track_service.upload_track.assert_not_called()

    def test_wrong_extension_is_rejected_by_the_form(self):
        data = {'title': 'Sněžka', 'description': '', 'participants': '',
                'parking_json': '', 'high_point_json': '',
                'gpx_file': SimpleUploadedFile('activity.fit', b'\x00\x01', content_type='application/octet-stream')}
        response = self.client.post(reverse('trips:trip_create'), data)

        self.assertEqual(response.status_code, 200)
        self.table.create_trip.assert_not_called()

    def test_saving_without_a_gpx_touches_no_blob(self):
        response = self.post_new_trip()

        self.assertRedirects(
            response,
            reverse('trips:trip_detail', kwargs={'trip_id': 'trip_20250712060000'}),
            fetch_redirect_response=False,
        )
        self.table.create_trip.assert_called_once()
        self.track_service.upload_track.assert_not_called()

    def post_to_existing_trip(self, **extra):
        self.table.get_trip_by_id.return_value = {
            'row_key': 'trip_existing', 'title': 'Sněžka', 'description': '',
            'trip_completed_on': '2024-01-02', 'location': '', 'difficulty': '',
            'parking_json': '', 'high_point_json': '', 'track_json': '',
        }
        data = {'title': 'Sněžka', 'description': '', 'participants': '',
                'parking_json': '', 'high_point_json': '',
                'gpx_file': SimpleUploadedFile('activity.gpx', self.GPX,
                                               content_type='application/gpx+xml')}
        data.update(extra)
        return self.client.post(
            reverse('trips:trip_edit', kwargs={'trip_id': 'trip_existing'}), data
        )

    def test_existing_values_send_the_user_to_the_confirmation_page(self):
        response = self.post_to_existing_trip(trip_completed_on='2024-01-02')

        self.assertRedirects(
            response,
            reverse('trips:trip_track_apply', kwargs={'trip_id': 'trip_existing'}),
            fetch_redirect_response=False,
        )

        trip_id, _, _ = self.track_service.upload_track.call_args[0]
        self.assertEqual(trip_id, 'trip_existing')

        row_key, trip_data = self.table.update_trip.call_args[0]
        self.assertEqual(row_key, 'trip_existing')
        # Blanks filled, the date the trip already had untouched
        self.assertEqual(trip_data['meters_ascend'], 500)
        self.assertEqual(json.loads(trip_data['high_point_json'])['Latitude'], 50.001)
        self.assertEqual(trip_data['trip_completed_on'].isoformat(), '2024-01-02')

    def test_an_all_blank_existing_trip_is_filled_without_asking(self):
        response = self.post_to_existing_trip()

        self.assertRedirects(
            response,
            reverse('trips:trip_edit', kwargs={'trip_id': 'trip_existing'}),
            fetch_redirect_response=False,
        )

        _, trip_data = self.table.update_trip.call_args[0]
        self.assertEqual(trip_data['meters_ascend'], 500)
        self.assertEqual(trip_data['trip_completed_on'], '2025-07-12')


class TrackApplyTests(TestCase):
    """The confirmation step before a track overwrites entered values."""

    TRIP_ID = 'trip_existing'

    STATS = {
        'distance_km': 12.4, 'meters_ascend': 500, 'meters_descend': 400,
        'duration_hours': 0.5, 'point_count': 3, 'original_point_count': 3,
        'min_elevation': 400, 'max_elevation': 900,
        'bounds': [[50.0, 14.0], [50.002, 14.0]], 'name': 'Sněžka',
        'start': {'Latitude': 50.0, 'Longtitude': 14.0},
        'high_point': {'Latitude': 50.001, 'Longtitude': 14.0, 'Elevation': 900},
        'started_on': '2025-07-12',
    }

    def setUp(self):
        self.user = User.objects.create_user('baruch', password='x', is_staff=True)
        self.client.force_login(self.user)

        self.table = mock.Mock()
        self.table.update_trip.return_value = (True, "")
        self.table.get_trip_by_id.return_value = {
            'row_key': self.TRIP_ID, 'title': 'Sněžka',
            'meters_ascend': 111, 'meters_descend': 0, 'length_hours': 9,
            'trip_completed_on': '2024-01-02',
            'parking_json': '', 'high_point_json': '',
            'track_json': json.dumps(self.STATS),
        }

        patch = mock.patch('trips.views.get_data_service', return_value=self.table)
        patch.start()
        self.addCleanup(patch.stop)

        self.url = reverse('trips:trip_track_apply', kwargs={'trip_id': self.TRIP_ID})

    def test_page_lists_only_the_fields_that_would_change(self):
        response = self.client.get(self.url)

        self.assertEqual(response.status_code, 200)
        fields = [c['field'] for c in response.context['conflicts']]
        # Ascent, duration and date differ; descent is 0 and the points are
        # blank, so those were filled in already and are not up for debate.
        self.assertEqual(set(fields), {'meters_ascend', 'length_hours', 'trip_completed_on'})

        self.assertContains(response, '111 m')      # current
        self.assertContains(response, '500 m')      # from the GPX
        self.assertContains(response, '2024-01-02')
        self.assertContains(response, '2025-07-12')

    def test_confirming_writes_only_the_selected_fields(self):
        response = self.client.post(self.url, {'fields': ['meters_ascend', 'length_hours']})

        self.assertRedirects(
            response,
            reverse('trips:trip_edit', kwargs={'trip_id': self.TRIP_ID}),
            fetch_redirect_response=False,
        )

        _, updates = self.table.update_trip.call_args[0]
        self.assertEqual(updates, {'meters_ascend': 500, 'length_hours': 0.5})

    def test_declining_writes_nothing(self):
        response = self.client.post(self.url, {})

        self.assertEqual(response.status_code, 302)
        self.table.update_trip.assert_not_called()

    def test_a_trip_whose_values_already_match_skips_the_page(self):
        self.table.get_trip_by_id.return_value = {
            'row_key': self.TRIP_ID, 'title': 'Sněžka',
            'meters_ascend': 500, 'meters_descend': 400, 'length_hours': 0.5,
            'trip_completed_on': '2025-07-12',
            'parking_json': json.dumps(self.STATS['start']),
            'high_point_json': json.dumps(self.STATS['high_point']),
            'track_json': json.dumps(self.STATS),
        }

        response = self.client.get(self.url)

        self.assertRedirects(
            response,
            reverse('trips:trip_edit', kwargs={'trip_id': self.TRIP_ID}),
            fetch_redirect_response=False,
        )

    def test_requires_staff(self):
        self.client.logout()

        response = self.client.get(self.url)

        self.assertEqual(response.status_code, 302)
        self.assertIn('/accounts/login/', response['Location'])


class TrackViewTests(TestCase):
    TRIP_ID = 'trip_20250712060000'

    def setUp(self):
        self.track_service = mock.Mock()
        patch = mock.patch('trips.views.AzureTrackService', return_value=self.track_service)
        patch.start()
        self.addCleanup(patch.stop)

    def test_geojson_is_served_to_anonymous_visitors(self):
        self.track_service.download_track.return_value = b'{"type":"Feature"}'

        response = self.client.get(
            reverse('trips:trip_track_geojson', kwargs={'trip_id': self.TRIP_ID})
        )

        self.assertEqual(response.status_code, 200)
        self.assertEqual(response['Content-Type'], 'application/geo+json')

    def test_missing_track_returns_404(self):
        self.track_service.download_track.return_value = None

        response = self.client.get(
            reverse('trips:trip_track_geojson', kwargs={'trip_id': self.TRIP_ID})
        )

        self.assertEqual(response.status_code, 404)

    def test_gpx_download_is_an_attachment(self):
        self.track_service.download_track.return_value = b'<gpx></gpx>'

        response = self.client.get(
            reverse('trips:trip_track_download', kwargs={'trip_id': self.TRIP_ID})
        )

        self.assertEqual(response.status_code, 200)
        self.assertIn('attachment', response['Content-Disposition'])
        self.assertIn(f'{self.TRIP_ID}.gpx', response['Content-Disposition'])

    def test_track_delete_requires_staff(self):
        response = self.client.post(
            reverse('trips:trip_track_delete', kwargs={'trip_id': self.TRIP_ID})
        )

        self.assertEqual(response.status_code, 302)
        self.assertIn('/accounts/login/', response['Location'])
        self.track_service.delete_track.assert_not_called()

    def test_track_delete_clears_the_stored_statistics(self):
        user = User.objects.create_user('baruch', password='x', is_staff=True)
        self.client.force_login(user)
        table = mock.Mock()
        table.merge_trip_fields.return_value = (True, "")

        with mock.patch('trips.views.get_data_service', return_value=table):
            response = self.client.post(
                reverse('trips:trip_track_delete', kwargs={'trip_id': self.TRIP_ID})
            )

        self.assertEqual(response.status_code, 302)
        self.track_service.delete_track.assert_called_once_with(self.TRIP_ID)
        table.merge_trip_fields.assert_called_once_with(self.TRIP_ID, {'TrackJson': ''})
