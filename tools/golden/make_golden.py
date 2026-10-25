"""Generate golden GPX fixtures and expected outputs from trips/gpx_service.py.

Run from the repo root (nothing is installed on the host):

    docker run --rm -v <repo>:/work -w /work python:3.12-slim \
        sh -c "pip install -q gpxpy && python tools/golden/make_golden.py"

gpx_service.py does not need Django, so it is loaded straight from its path.
"""
import importlib.util
import json
import math
import os
from datetime import datetime, timedelta, timezone

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, 'tests', 'BaruchsTreks.Tests', 'Fixtures', 'gpx')

spec = importlib.util.spec_from_file_location('gpx_service', os.path.join(ROOT, 'trips', 'gpx_service.py'))
gpx_service = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gpx_service)

START = datetime(2025, 7, 12, 6, 0, 0, tzinfo=timezone.utc)
NS11 = 'xmlns="http://www.topografix.com/GPX/1/1"'


def make_gpx(points, tag='trk', ns=NS11, version='1.1', encoding='UTF-8', name='Test', with_time=True, with_ele=True):
    point_tag = 'trkpt' if tag == 'trk' else 'rtept'
    body = []
    for lat, lng, elevation, offset in points:
        inner = ''
        if with_ele:
            inner += f'<ele>{elevation}</ele>'
        if with_time:
            stamp = (START + timedelta(seconds=offset)).strftime('%Y-%m-%dT%H:%M:%SZ')
            inner += f'<time>{stamp}</time>'
        body.append(f'<{point_tag} lat="{lat}" lon="{lng}">{inner}</{point_tag}>')
    joined = ''.join(body)
    if tag == 'trk':
        content = f'<trk><name>{name}</name><trkseg>{joined}</trkseg></trk>'
    else:
        content = f'<rte><name>{name}</name>{joined}</rte>'
    return (
        f'<?xml version="1.0" encoding="{encoding}"?>'
        f'<gpx version="{version}" creator="test" {ns}>{content}</gpx>'
    )


def wiggle(n, amplitude=0.0004, step=0.00005, ele_base=300.0, ele_amp=80.0, seconds=5):
    pts = []
    for i in range(n):
        lat = 50.0 + i * step
        lng = 14.0 + amplitude * math.sin(i / 7.0)
        ele = round(ele_base + ele_amp * math.sin(i / 40.0) + 0.37 * (i % 5), 1)
        pts.append((round(lat, 6), round(lng, 6), ele, i * seconds))
    return pts


def hike():
    # Realistic trk: time + elevation, 600 points with a climb and descent.
    pts = []
    for i in range(600):
        lat = 50.7360 + i * 0.00012 + 0.0003 * math.sin(i / 11.0)
        lng = 15.7400 + i * 0.00018 + 0.0004 * math.cos(i / 17.0)
        ele = 800 + 600 * math.sin(math.pi * i / 600.0) + 1.3 * math.sin(i * 1.7)
        pts.append((round(lat, 6), round(lng, 6), round(ele, 1), i * 30))
    return pts


samples = {}  # name -> bytes

# Samples taken from trips/tests.py
samples['stats'] = make_gpx([
    (50.000, 14.000, 400, 0), (50.001, 14.000, 500, 600),
    (50.002, 14.000, 700, 1200), (50.003, 14.000, 450, 1800)]).encode('utf-8')
samples['high_point'] = make_gpx([
    (50.000, 14.000, 400, 0), (50.001, 14.000, 900, 600), (50.002, 14.000, 500, 1200)]).encode('utf-8')
samples['elevation_noise'] = make_gpx(
    [(50.0 + i * 0.0001, 14.0, 400 + (i % 2), i * 10) for i in range(40)]).encode('utf-8')
samples['collinear'] = make_gpx(
    [(50.0 + i * 0.0005, 14.0, 400, i * 10) for i in range(50)]).encode('utf-8')
samples['turn'] = make_gpx([
    (50.000, 14.000, 400, 0), (50.005, 14.000, 400, 600),
    (50.005, 14.010, 400, 1200), (50.010, 14.010, 400, 1800)]).encode('utf-8')
samples['route_only'] = make_gpx(
    [(50.000, 14.000, 400, 0), (50.002, 14.000, 600, 1200)], tag='rte').encode('utf-8')
samples['bounds'] = make_gpx([
    (50.000, 14.000, 400, 0), (50.010, 14.020, 500, 600), (49.990, 14.010, 450, 1200)]).encode('utf-8')
samples['no_elevation_tests'] = (
    '<?xml version="1.0" encoding="UTF-8"?>'
    f'<gpx version="1.1" creator="test" {NS11}>'
    '<trk><trkseg><trkpt lat="50.0" lon="14.0"></trkpt><trkpt lat="50.002" lon="14.0"></trkpt></trkseg></trk></gpx>'
).encode('utf-8')

# Three realistic-looking samples plus latin-1
samples['hike_time_ele'] = make_gpx(hike(), name='Sněžka z Pece').encode('utf-8')
samples['route_no_time'] = make_gpx(
    wiggle(300, seconds=0), tag='rte', with_time=False, name='Plánovaná trasa').encode('utf-8')
samples['track_no_ele'] = make_gpx(wiggle(250), with_ele=False, name='Bez elevace').encode('utf-8')
samples['latin1'] = make_gpx(
    [(50.0, 14.0, 400, 0), (50.002, 14.001, 450, 600), (50.004, 14.0, 420, 1200)],
    encoding='ISO-8859-1', name='Café Müller').encode('latin-1')

# Other dialects and shapes
samples['gpx10'] = make_gpx(
    [(50.000, 14.000, 400, 0), (50.001, 14.000, 500, 600), (50.002, 14.000, 700, 1200)],
    ns='xmlns="http://www.topografix.com/GPX/1/0"', version='1.0').encode('utf-8')
samples['no_namespace'] = make_gpx(
    [(50.000, 14.000, 400, 0), (50.001, 14.000, 500, 600), (50.002, 14.000, 700, 1200)], ns='').encode('utf-8')
samples['utf8_bom'] = b'\xef\xbb\xbf' + make_gpx(
    [(50.000, 14.000, 400, 0), (50.002, 14.000, 600, 1200)], name='Bom').encode('utf-8')
samples['metadata_name_offset_time'] = (
    '<?xml version="1.0" encoding="UTF-8"?>'
    f'<gpx version="1.1" creator="test" {NS11}><metadata><name>Metadata jmeno</name></metadata>'
    '<trk><trkseg><trkpt lat="50.0" lon="14.0"><ele>400</ele><time>2025-07-13T00:30:00+02:00</time></trkpt>'
    '<trkpt lat="50.002" lon="14.0"><ele>420</ele><time>2025-07-13T01:10:00+02:00</time></trkpt>'
    '</trkseg></trk></gpx>'
).encode('utf-8')
samples['multi_segment'] = (
    '<?xml version="1.0" encoding="UTF-8"?>'
    f'<gpx version="1.1" creator="test" {NS11}>'
    '<trk><name>A</name><trkseg><trkpt lat="50.0" lon="14.0"><ele>400</ele></trkpt>'
    '<trkpt lat="50.001" lon="14.0"><ele>410</ele></trkpt></trkseg>'
    '<trkseg><trkpt lat="50.01" lon="14.01"><ele>430</ele></trkpt>'
    '<trkpt lat="50.011" lon="14.01"><ele>470</ele></trkpt></trkseg></trk>'
    '<trk><name>B</name><trkseg><trkpt lat="50.02" lon="14.02"><ele>500</ele></trkpt></trkseg></trk>'
    '<rte><rtept lat="1" lon="1"></rtept></rte></gpx>'
).encode('utf-8')
# A zigzag wider than the 5 m tolerance: nothing simplifies away, so the 2000 point cap kicks in.
samples['dense_capped'] = make_gpx(
    [(round(50.0 + i * 0.00002 + 0.000004 * math.sin(i * 1.3), 6),
      round(14.0 + (0.0003 if i % 2 else 0.0) + 0.00003 * math.sin(i * 0.7), 6),
      300.0 + (i % 7), i * 2)
     for i in range(4000)],
    name='Dense').encode('utf-8')

os.makedirs(OUT, exist_ok=True)
for name, raw in samples.items():
    parsed = gpx_service.parse_gpx(raw)
    expected = {
        'track_json': gpx_service.track_record(parsed),
        'geojson': parsed['geojson'],
    }
    with open(os.path.join(OUT, name + '.gpx'), 'wb') as f:
        f.write(raw)
    with open(os.path.join(OUT, name + '.expected.json'), 'w', encoding='utf-8', newline='\n') as f:
        json.dump(expected, f, ensure_ascii=False, indent=1)
        f.write('\n')
    print(name, parsed['stats']['original_point_count'], '->', parsed['stats']['point_count'])

# build_gpx golden: fixed input, the exact XML bytes.
build_points = [
    (50.0, 14.0, 400.0, START),
    (50.001, 14.0, 500.5, START + timedelta(seconds=900)),
    (50.002, 14.0, None, None),
]
with open(os.path.join(OUT, 'build.expected.xml'), 'wb') as f:
    f.write(gpx_service.build_gpx('Ranní výběh', build_points, START))
