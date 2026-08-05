"""Parsing GPX tracks and deriving trip statistics.

GPX files (typically exported from Garmin Connect or built from Strava streams)
are too large for Azure Table Storage, so the raw file lives in Blob Storage.
This module turns a GPX document into:

* a simplified polyline small enough to render on the trip map,
* summary statistics used to pre-fill the trip form.
"""

import json
import logging
import math

import gpxpy
import gpxpy.gpx

logger = logging.getLogger(__name__)

# Simplification targets - a Garmin track can easily hold 20k points, which is
# far more detail than a 400px tall map can show.
SIMPLIFY_TOLERANCE_M = 5.0
MAX_TRACK_POINTS = 2000

# Ignore elevation wobble below this many meters, otherwise GPS noise inflates
# the ascent of a flat walk by hundreds of meters.
ELEVATION_THRESHOLD_M = 3.0

EARTH_RADIUS_M = 6371000.0


class GpxParseError(Exception):
    """Raised when a file cannot be read as GPX."""


def _decode(raw):
    if isinstance(raw, str):
        return raw
    for encoding in ('utf-8-sig', 'utf-8', 'latin-1'):
        try:
            return raw.decode(encoding)
        except UnicodeDecodeError:
            continue
    raise GpxParseError("Soubor není v čitelném textovém kódování.")


def _collect_points(gpx):
    """Return [(lat, lng, elevation_or_None, time_or_None), ...].

    Garmin exports recorded activities as <trk>, but courses and planned routes
    come out as <rte>, so both are accepted.
    """
    points = []
    for track in gpx.tracks:
        for segment in track.segments:
            for point in segment.points:
                points.append((point.latitude, point.longitude, point.elevation, point.time))

    if not points:
        for route in gpx.routes:
            for point in route.points:
                points.append((point.latitude, point.longitude, point.elevation, point.time))

    return points


def _haversine(lat1, lng1, lat2, lng2):
    """Great-circle distance in meters."""
    phi1, phi2 = math.radians(lat1), math.radians(lat2)
    d_phi = phi2 - phi1
    d_lambda = math.radians(lng2 - lng1)
    a = math.sin(d_phi / 2) ** 2 + math.cos(phi1) * math.cos(phi2) * math.sin(d_lambda / 2) ** 2
    return 2 * EARTH_RADIUS_M * math.asin(min(1.0, math.sqrt(a)))


def _total_distance_m(points):
    """Horizontal distance along the track.

    Ground distance, not slope distance, so the number matches what Garmin
    Connect and Strava report for the same activity.
    """
    total = 0.0
    for (lat1, lng1, _, _), (lat2, lng2, _, _) in zip(points, points[1:]):
        total += _haversine(lat1, lng1, lat2, lng2)
    return total


def _elevation_gain(points, threshold=ELEVATION_THRESHOLD_M):
    """Accumulate ascent/descent with hysteresis, ignoring sub-threshold noise."""
    ascend = descend = 0.0
    reference = None
    for _, _, elevation, _ in points:
        if elevation is None:
            continue
        if reference is None:
            reference = elevation
            continue
        delta = elevation - reference
        if delta >= threshold:
            ascend += delta
            reference = elevation
        elif delta <= -threshold:
            descend += -delta
            reference = elevation
    return ascend, descend


def _perpendicular_distance_m(point, start, end):
    """Distance from point to the start-end segment, on a local flat projection."""
    lat_scale = math.cos(math.radians(start[0])) or 1e-9
    px = (point[1] - start[1]) * lat_scale
    py = point[0] - start[0]
    ex = (end[1] - start[1]) * lat_scale
    ey = end[0] - start[0]

    segment_len_sq = ex * ex + ey * ey
    if segment_len_sq == 0:
        closest_x, closest_y = 0.0, 0.0
    else:
        t = max(0.0, min(1.0, (px * ex + py * ey) / segment_len_sq))
        closest_x, closest_y = t * ex, t * ey

    dx, dy = px - closest_x, py - closest_y
    return math.hypot(dx, dy) * math.pi / 180 * EARTH_RADIUS_M


def _simplify(points, tolerance_m=SIMPLIFY_TOLERANCE_M):
    """Ramer-Douglas-Peucker, iterative so long tracks cannot blow the stack."""
    if len(points) < 3:
        return list(points)

    keep = [False] * len(points)
    keep[0] = keep[-1] = True
    stack = [(0, len(points) - 1)]

    while stack:
        first, last = stack.pop()
        if last <= first + 1:
            continue
        max_distance, index = 0.0, first
        for i in range(first + 1, last):
            distance = _perpendicular_distance_m(points[i], points[first], points[last])
            if distance > max_distance:
                max_distance, index = distance, i
        if max_distance > tolerance_m:
            keep[index] = True
            stack.append((first, index))
            stack.append((index, last))

    return [point for point, kept in zip(points, keep) if kept]


def _cap_point_count(points, limit=MAX_TRACK_POINTS):
    """Evenly thin an already simplified track that is still too dense."""
    if len(points) <= limit:
        return points
    step = len(points) / float(limit)
    thinned = [points[int(i * step)] for i in range(limit)]
    thinned[-1] = points[-1]
    return thinned


def parse_gpx(raw):
    """Parse a GPX document into map geometry plus summary statistics.

    Args:
        raw: bytes or str containing a GPX document.

    Returns:
        dict with the simplified ``points``, a GeoJSON ``Feature`` and the
        derived ``stats``.

    Raises:
        GpxParseError: the file is not valid GPX or holds no coordinates.
    """
    text = _decode(raw)

    try:
        gpx = gpxpy.parse(text)
    except Exception as e:
        raise GpxParseError(f"Soubor se nepodařilo přečíst jako GPX: {e}")

    points = _collect_points(gpx)
    if not points:
        raise GpxParseError("GPX soubor neobsahuje žádné body trasy.")

    distance_m = _total_distance_m(points)
    ascend, descend = _elevation_gain(points)

    times = [time for _, _, _, time in points if time is not None]
    duration_hours = None
    started_at = None
    if times:
        started_at = min(times)
        duration_hours = round((max(times) - started_at).total_seconds() / 3600.0, 2)

    elevations = [elevation for _, _, elevation, _ in points if elevation is not None]
    high_point = None
    if elevations:
        peak = max(
            (point for point in points if point[2] is not None),
            key=lambda point: point[2],
        )
        high_point = {'Latitude': peak[0], 'Longtitude': peak[1], 'Elevation': round(peak[2], 1)}

    start_lat, start_lng = points[0][0], points[0][1]

    simplified = _cap_point_count(_simplify(points))
    logger.info("Parsed GPX: %d points simplified to %d", len(points), len(simplified))

    lats = [point[0] for point in simplified]
    lngs = [point[1] for point in simplified]

    stats = {
        'distance_km': round(distance_m / 1000.0, 2),
        'meters_ascend': int(round(ascend)),
        'meters_descend': int(round(descend)),
        'duration_hours': duration_hours,
        'point_count': len(simplified),
        'original_point_count': len(points),
        'min_elevation': int(round(min(elevations))) if elevations else None,
        'max_elevation': int(round(max(elevations))) if elevations else None,
        'bounds': [[min(lats), min(lngs)], [max(lats), max(lngs)]],
        'name': gpx.name or (gpx.tracks[0].name if gpx.tracks else None),
    }

    return {
        'points': [
            [round(lat, 6), round(lng, 6)] + ([round(elevation, 1)] if elevation is not None else [])
            for lat, lng, elevation, _ in simplified
        ],
        'geojson': {
            'type': 'Feature',
            'properties': stats,
            'geometry': {
                'type': 'LineString',
                'coordinates': [
                    [round(lng, 6), round(lat, 6)] + ([round(elevation, 1)] if elevation is not None else [])
                    for lat, lng, elevation, _ in simplified
                ],
            },
        },
        'stats': stats,
        'start': {'Latitude': start_lat, 'Longtitude': start_lng},
        'high_point': high_point,
        'started_at': started_at,
    }


def geojson_bytes(parsed):
    """Serialize the parsed track's GeoJSON for storage."""
    return json.dumps(parsed['geojson'], separators=(',', ':')).encode('utf-8')


def track_record(parsed):
    """The statistics stored in the table alongside the track.

    On top of the summary numbers this carries the points the trip form can be
    filled from, so offering those values again later needs neither the GPX file
    nor a re-parse.
    """
    record = dict(parsed['stats'])
    record['start'] = parsed['start']
    if parsed.get('high_point'):
        record['high_point'] = parsed['high_point']
    if parsed.get('started_at'):
        record['started_on'] = parsed['started_at'].date().isoformat()
    return record


def build_gpx(name, points, activity_time=None):
    """Build a GPX document from ``(lat, lng, elevation, time)`` tuples.

    Used to turn Strava activity streams, which are plain arrays, into the same
    GPX format an uploaded Garmin file arrives in.
    """
    gpx = gpxpy.gpx.GPX()
    gpx.name = name
    gpx.creator = "Baruch's Treks"

    track = gpxpy.gpx.GPXTrack(name=name)
    gpx.tracks.append(track)
    segment = gpxpy.gpx.GPXTrackSegment()
    track.segments.append(segment)

    for lat, lng, elevation, time in points:
        segment.points.append(
            gpxpy.gpx.GPXTrackPoint(latitude=lat, longitude=lng, elevation=elevation, time=time)
        )

    if activity_time:
        gpx.time = activity_time

    return gpx.to_xml().encode('utf-8')
