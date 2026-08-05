from django.shortcuts import render, redirect
from django.contrib import messages
from django.conf import settings
import json
import secrets
import traceback
import logging
from datetime import datetime, timezone
from django.http import HttpResponse, HttpResponseServerError, JsonResponse
from django.contrib.auth.decorators import login_required, user_passes_test
from django.urls import reverse
from django.views.decorators.http import require_POST

from .azure_service import AzureTableService
from .blob_service import AzureBlobService, AzureTrackService
from .forms import TripEditForm
from .gpx_service import GpxParseError, geojson_bytes, parse_gpx, track_record
from .strava_service import (
    StravaError,
    StravaNotConfigured,
    StravaNotConnected,
    StravaService,
)

logger = logging.getLogger(__name__)

@login_required
@user_passes_test(lambda u: u.is_staff)
def trip_delete(request, trip_id):
    """Delete a trip and redirect to all trips with a message."""
    if request.method == 'POST':
        service = get_data_service()
        try:
            table_client = service.get_table_client()
            table_client.delete_entity(partition_key='Trips', row_key=trip_id)
            AzureTrackService().delete_track(trip_id)
            messages.success(request, 'Trip deleted successfully.')
        except Exception as e:
            logger.error(f"Error deleting trip {trip_id}: {str(e)}", exc_info=True)
            messages.error(request, f'Failed to delete trip: {str(e)}')
        return redirect('trips:all_trips')
    else:
        # Show confirmation page or redirect if GET
        return render(request, 'trips/confirm_delete.html', {'trip_id': trip_id})

@login_required
@user_passes_test(lambda u: u.is_staff)
def trip_copy(request, trip_id):
    """Copy a trip (except photos), add ' (copy)' to the title, and redirect to edit page for the new trip."""
    service = get_data_service()
    trip = service.get_trip_by_id(trip_id)
    if not trip:
        return HttpResponse('Original trip not found.', status=404)

    # Prepare new trip data (exclude photos, add ' (copy)' to title)
    new_trip_data = trip.copy()
    new_trip_data['title'] = f"{trip.get('title', '')} (copy)"
    new_trip_data.pop('row_key', None)
    new_trip_data.pop('partition_key', None)
    # Remove or reset any fields you do not want to copy (e.g., completion date, etc.)
    # Optionally, clear trip_completed_on or other fields if needed
    # new_trip_data['trip_completed_on'] = None

    # Create the new trip
    created, error_msg, new_row_key = service.create_trip(new_trip_data)
    if not created:
        return HttpResponse(f'Failed to copy trip: {error_msg}', status=500)

    # Redirect to edit page for the new trip
    return redirect(reverse('trips:trip_edit', kwargs={'trip_id': new_row_key}))

def get_data_service():
    """Get the Azure Table service"""
    print("Creating AzureTableService...")
    connection_string = settings.AZURE_STORAGE_CONNECTION_STRING
    print(f"Connection string (first 20 chars): {connection_string[:20]}...")
    return AzureTableService(
        connection_string=connection_string,
        table_name=settings.AZURE_TABLE_NAME
    )

def get_strava_service():
    """Get the Strava service, backed by the Azure table for token storage"""
    return StravaService(table_service=get_data_service())


def track_stats(trip):
    """Return the stored GPX statistics for a trip, or None."""
    raw = (trip or {}).get('track_json')
    if not raw:
        return None
    try:
        return json.loads(raw)
    except (TypeError, ValueError):
        logger.warning(f"Could not parse track_json for trip {trip.get('row_key')}")
        return None


# Trip fields a GPX can supply, in the order they are listed for confirmation
GPX_FIELDS = (
    ('meters_ascend', 'Převýšení nahoru'),
    ('meters_descend', 'Převýšení dolů'),
    ('length_hours', 'Délka trvání'),
    ('trip_completed_on', 'Datum'),
    ('parking_json', 'Bod startu'),
    ('high_point_json', 'Vrchol'),
)


def derived_from_track(stats):
    """The trip field values a stored track can supply, as {field: value}."""
    derived = {}
    if not stats:
        return derived

    if stats.get('meters_ascend') is not None:
        derived['meters_ascend'] = stats['meters_ascend']
    if stats.get('meters_descend') is not None:
        derived['meters_descend'] = stats['meters_descend']
    if stats.get('duration_hours'):
        derived['length_hours'] = stats['duration_hours']
    if stats.get('started_on'):
        derived['trip_completed_on'] = stats['started_on']
    if stats.get('start'):
        derived['parking_json'] = json.dumps(stats['start'])
    if stats.get('high_point'):
        derived['high_point_json'] = json.dumps(stats['high_point'])

    return derived


def _is_blank(value):
    """A zero ascent or an empty string both mean 'nothing entered here yet'."""
    if value is None:
        return True
    if isinstance(value, str):
        return value.strip() in ('', 'none')
    if isinstance(value, bool):
        return not value
    if isinstance(value, (int, float)):
        return value == 0
    return False


def _coordinates(value):
    """Read a coordinate pair out of a stored JSON string, rounded for compare."""
    try:
        data = value if isinstance(value, dict) else json.loads(value)
        return (round(float(data['Latitude']), 5), round(float(data['Longtitude']), 5))
    except (TypeError, ValueError, KeyError):
        return None


def _as_date_text(value):
    return value.isoformat() if hasattr(value, 'isoformat') else str(value)[:10]


def _same_value(field, current, new):
    if field in ('parking_json', 'high_point_json'):
        return _coordinates(current) == _coordinates(new)
    if field == 'trip_completed_on':
        return _as_date_text(current) == _as_date_text(new)
    try:
        return abs(float(current) - float(new)) < 0.01
    except (TypeError, ValueError):
        return str(current) == str(new)


def _display_value(field, value):
    """Render a value the way it is shown on the confirmation page."""
    if field in ('parking_json', 'high_point_json'):
        coordinates = _coordinates(value)
        if not coordinates:
            return '—'
        text = f"{coordinates[0]:.5f}, {coordinates[1]:.5f}"
        try:
            data = value if isinstance(value, dict) else json.loads(value)
            if data.get('Elevation'):
                text += f" ({round(data['Elevation'])} m)"
        except (TypeError, ValueError):
            pass
        return text
    if field == 'trip_completed_on':
        return _as_date_text(value)
    if field == 'length_hours':
        return f"{value} h"
    return f"{value} m"


def compare_with_track(derived, current):
    """Split GPX values into ones that fill a blank and ones that would overwrite.

    Filling a blank costs the user nothing, so it happens straight away. Anything
    that would replace a value already entered is returned for confirmation.
    """
    fills = {}
    conflicts = []

    for field, label in GPX_FIELDS:
        if field not in derived:
            continue

        new = derived[field]
        existing = current.get(field)

        if _is_blank(existing):
            fills[field] = new
        elif not _same_value(field, existing, new):
            conflicts.append({
                'field': field,
                'label': label,
                'current': _display_value(field, existing),
                'new': _display_value(field, new),
            })

    return fills, conflicts


def index(request):
    """Landing page with trip previews"""
    try:
        print("Index view called")
        print(f"AZURE_STORAGE_CONNECTION_STRING: {settings.AZURE_STORAGE_CONNECTION_STRING[:20]}...")
        print(f"AZURE_TABLE_NAME: {settings.AZURE_TABLE_NAME}")
        
        service = get_data_service()
        blob_service = AzureBlobService()
        trips = service.get_all_trips()
        
        if trips and len(trips) > 0:
            print(f"Retrieved {len(trips)} trips")
            print("First trip values:")
            for key, value in trips[0].items():
                print(f"  {key}: {value}")
        
        # Note: Trips are already sorted by timestamp (newest first) in the AzureTableService
        
        # Take only the first 6 trips for the landing page
        featured_trips = trips[:6]
        print(f"Selected {len(featured_trips)} featured trips")
        
        # Get the first photo for each trip
        for trip in featured_trips:
            photos = blob_service.list_photos(trip['row_key'])
            trip['first_photo'] = photos[0] if photos else None
        
        return render(request, 'trips/index.html', {
            'trips': featured_trips,
        })
    except Exception as e:
        print(f"Error in index view: {e}")
        print(traceback.format_exc())
        # Return a simple error page instead of crashing
        return render(request, 'trips/error.html', {
            'error': str(e),
            'traceback': traceback.format_exc()
        })

def trip_detail(request, trip_id):
    """Detail page for a specific trip"""
    service = get_data_service()
    trip = service.get_trip_by_id(trip_id)
    
    if not trip:
        return render(request, 'trips/not_found.html')
    
    # Parse JSON coordinates for map display
    parking_coords = None
    high_point_coords = None
    
    if trip.get('parking_json'):
        try:
            parking_data = json.loads(trip['parking_json'])
            parking_coords = {
                'lat': parking_data.get('Latitude'),
                'lng': parking_data.get('Longtitude')  # Match the spelling in the database
            }
        except:
            pass
    
    if trip.get('high_point_json'):
        try:
            high_point_data = json.loads(trip['high_point_json'])
            high_point_coords = {
                'lat': high_point_data.get('Latitude'),
                'lng': high_point_data.get('Longtitude')  # Match the spelling in the database
            }
        except:
            pass
    
    # Get trip photos
    blob_service = AzureBlobService()
    trip_photos = blob_service.list_photos(trip_id)
    
    return render(request, 'trips/detail.html', {
        'trip': trip,
        'parking_coords': json.dumps(parking_coords) if parking_coords else None,
        'high_point_coords': json.dumps(high_point_coords) if high_point_coords else None,
        'mapy_cz_api_key': settings.MAPY_CZ_API_KEY,
        'trip_photos': trip_photos,
        'track_stats': track_stats(trip),
    })

def admin_required(view_func):
    """
    Decorator that checks if a user is both authenticated and an admin.
    Combines login_required and user_passes_test to ensure the user is logged in and is an admin.
    """
    # Check if user is an admin
    def check_admin(user):
        return user.is_staff or user.is_superuser
    
    # Apply both decorators
    decorated_view = login_required(user_passes_test(check_admin)(view_func))
    return decorated_view

@admin_required
def trip_edit(request, trip_id=None):
    """View function for creating or editing a trip"""
    try:
        # Get Azure services
        azure_service = get_data_service()
        blob_service = AzureBlobService()
        
        # Check if we're editing an existing trip or creating a new one
        is_new_trip = trip_id is None
        trip = None
        trip_photos = []
        
        if not is_new_trip:
            # Get existing trip from Azure Table Storage
            trip = azure_service.get_trip_by_id(trip_id)
            
            if not trip:
                logger.warning(f"Trip with ID {trip_id} not found")
                return HttpResponse("Trip not found", status=404)
            
            # Get trip photos
            trip_photos = blob_service.list_photos(trip_id)
        
        # Process form submission
        if request.method == 'POST':
            form = TripEditForm(request.POST, request.FILES)
            
            if form.is_valid():
                # Get form data
                trip_data = {
                    'title': form.cleaned_data['title'],
                    'description': form.cleaned_data['description'],
                    'trip_completed_on': form.cleaned_data['trip_completed_on'],
                    'length_hours': form.cleaned_data['length_hours'],
                    'participants': form.cleaned_data['participants'],
                    'meters_ascend': form.cleaned_data['meters_ascend'],
                    'meters_descend': form.cleaned_data['meters_descend'],
                    'uiaa_grade': form.cleaned_data['uiaa_grade'],
                    'alpine_grade': form.cleaned_data['alpine_grade'],
                    'trip_class': form.cleaned_data['trip_class'],
                    'ferata_grade': form.cleaned_data['ferata_grade'],
                    'parking_json': form.cleaned_data['parking_json'],
                    'high_point_json': form.cleaned_data['high_point_json'],
                }

                # Parse an uploaded GPX before saving, so its values can be
                # folded into the very same write.
                gpx_bytes = None
                parsed_gpx = None
                gpx_fills = {}
                gpx_conflicts = []
                gpx_file = form.cleaned_data.get('gpx_file')

                if gpx_file:
                    gpx_bytes = gpx_file.read()
                    try:
                        parsed_gpx = parse_gpx(gpx_bytes)
                    except GpxParseError as e:
                        logger.warning(f"Invalid GPX upload: {str(e)}")
                        return render(request, 'trips/edit.html', {
                            'form': form,
                            'trip': trip,
                            'trip_photos': trip_photos,
                            'is_new': is_new_trip,
                            'error': str(e),
                            'mapy_cz_api_key': settings.MAPY_CZ_API_KEY,
                            'track_stats': track_stats(trip),
                            'strava_configured': get_strava_service().is_configured,
                        })

                    record = track_record(parsed_gpx)
                    trip_data['track_json'] = json.dumps(record)

                    # Blanks are filled right away; anything that would replace
                    # an entered value waits for the user to confirm it.
                    gpx_fills, gpx_conflicts = compare_with_track(
                        derived_from_track(record), trip_data
                    )
                    trip_data.update(gpx_fills)

                if is_new_trip:
                    # Create new trip in Azure Table Storage
                    success, message, new_trip_id = azure_service.create_trip(trip_data)
                    
                    if not success:
                        logger.error(f"Error creating trip: {message}")
                        return render(request, 'trips/edit.html', {
                            'form': form,
                            'is_new': True,
                            'error': f"Error creating trip: {message}",
                            'mapy_cz_api_key': settings.MAPY_CZ_API_KEY,
                            'strava_configured': get_strava_service().is_configured,
                        })
                    
                    # Set trip_id to the newly created trip's ID
                    trip_id = new_trip_id
                else:
                    # Preserve existing location and difficulty values
                    if 'location' in trip and trip['location']:
                        trip_data['location'] = trip['location']
                    
                    if 'difficulty' in trip and trip['difficulty']:
                        trip_data['difficulty'] = trip['difficulty']
                    
                    # Update trip in Azure Table Storage
                    success, message = azure_service.update_trip(trip_id, trip_data)
                    
                    if not success:
                        logger.error(f"Error updating trip {trip_id}: {message}")
                        return render(request, 'trips/edit.html', {
                            'form': form,
                            'trip': trip,
                            'trip_photos': trip_photos,
                            'is_new': False,
                            'error': f"Error updating trip: {message}",
                            'mapy_cz_api_key': settings.MAPY_CZ_API_KEY,
                            'track_stats': track_stats(trip),
                            'strava_configured': get_strava_service().is_configured,
                        })
                
                # Store the GPX track now that the trip has an id
                if parsed_gpx:
                    track_service = AzureTrackService()
                    success, error = track_service.upload_track(
                        trip_id, gpx_bytes, geojson_bytes(parsed_gpx)
                    )
                    if success:
                        messages.success(
                            request,
                            f"Trasa nahrána: {parsed_gpx['stats']['distance_km']} km, "
                            f"↑{parsed_gpx['stats']['meters_ascend']} m."
                        )
                    else:
                        logger.error(f"Error uploading track: {error}")
                        messages.error(request, f"Trasu se nepodařilo uložit: {error}")

                # Handle photo uploads
                if request.FILES.getlist('photos'):
                    for photo_file in request.FILES.getlist('photos'):
                        success, result = blob_service.upload_photo(trip_id, photo_file)
                        if not success:
                            logger.warning(f"Error uploading photo: {result}")

                # The GPX wants to replace values that are already filled in, so
                # ask before touching them.
                if gpx_conflicts:
                    if gpx_fills:
                        messages.info(
                            request,
                            f'Prázdná pole ({len(gpx_fills)}) jsou předvyplněná z GPX.'
                        )
                    return redirect('trips:trip_track_apply', trip_id=trip_id)

                # Values taken from a GPX are a starting point, not a verdict -
                # the high point in particular is only the highest coordinate,
                # which is not always the summit the trip was about. So stay in
                # the editor with them filled in, ready to be corrected.
                if gpx_fills:
                    messages.info(
                        request,
                        'Prázdná pole jsou předvyplněná z GPX. Můžeš je upravit '
                        '(vrchol přetažením značky na mapě) a uložit znovu.'
                    )
                    return redirect('trips:trip_edit', trip_id=trip_id)

                # Redirect to trip detail page
                return redirect('trips:trip_detail', trip_id=trip_id)
            else:
                logger.warning(f"Form validation failed: {form.errors}")
        else:
            # Create form with initial values
            initial_data = {}
            
            if not is_new_trip:
                # Pre-populate form with existing trip data
                initial_data = {
                    'title': trip.get('title', ''),
                    'description': trip.get('description', ''),
                    'trip_completed_on': trip.get('trip_completed_on', ''),
                    'length_hours': trip.get('length_hours', ''),
                    'participants': trip.get('participants', ''),
                    'meters_ascend': trip.get('meters_ascend', ''),
                    'meters_descend': trip.get('meters_descend', ''),
                    'uiaa_grade': trip.get('uiaa_grade', ''),
                    'alpine_grade': trip.get('alpine_grade', ''),
                    'trip_class': trip.get('trip_class', ''),
                    'ferata_grade': trip.get('ferata_grade', ''),
                    'parking_json': trip.get('parking_json', ''),
                    'high_point_json': trip.get('high_point_json', ''),
                }
            
            form = TripEditForm(initial=initial_data)
        
        # Render the edit template with the form and trip data
        context = {
            'form': form,
            'is_new': is_new_trip,
            'mapy_cz_api_key': settings.MAPY_CZ_API_KEY,
            'strava_configured': get_strava_service().is_configured,
        }

        if not is_new_trip:
            context.update({
                'trip': trip,
                'trip_photos': trip_photos,
                'track_stats': track_stats(trip),
            })
        
        return render(request, 'trips/edit.html', context)
    except Exception as e:
        # Log the error
        logger.error(f"Error editing trip: {str(e)}")
        import traceback
        logger.error(traceback.format_exc())
        return HttpResponseServerError("An error occurred while editing the trip.")

def all_trips(request):
    """Page showing all trips"""
    service = get_data_service()
    all_trips = service.get_all_trips()
    
    # Get filter parameter from request
    trip_filter = request.GET.get('filter', 'all')
    # Sorting parameters
    sort_key = request.GET.get('sort', 'modified')  # modified | created | completed | name
    sort_dir = request.GET.get('dir', 'desc')       # asc | desc
    
    # Filter trips based on completion status
    if trip_filter == 'completed':
        filtered_trips = [trip for trip in all_trips if trip.get('trip_completed_on')]
    elif trip_filter == 'future':
        filtered_trips = [trip for trip in all_trips if not trip.get('trip_completed_on')]
    else:
        filtered_trips = all_trips
    
    # Sorting logic
    fallback_dt = datetime.min.replace(tzinfo=timezone.utc)
    if sort_key == 'created':
        key_func = lambda t: (t.get('rowkey_dt') or fallback_dt)
    elif sort_key == 'completed':
        key_func = lambda t: (t.get('completed_at') or fallback_dt)
    elif sort_key == 'name':
        key_func = lambda t: (t.get('title') or '').lower()
    else:  # 'modified' default
        key_func = lambda t: (t.get('modified_at') or t.get('rowkey_dt') or fallback_dt)

    reverse = (sort_dir != 'asc')
    try:
        filtered_trips = sorted(filtered_trips, key=key_func, reverse=reverse)
    except Exception as e:
        print(f"Sorting error: {e}")
    
    print(f"Filter: {trip_filter}, Total trips: {len(all_trips)}, Filtered trips: {len(filtered_trips)}")
    
    return render(request, 'trips/all_trips.html', {
        'trips': filtered_trips,
        'current_filter': trip_filter,
        'current_sort': sort_key,
        'current_dir': sort_dir,
        'completed_count': len([trip for trip in all_trips if trip.get('trip_completed_on')]),
        'future_count': len([trip for trip in all_trips if not trip.get('trip_completed_on')]),
        'total_count': len(all_trips),
    })

def trip_track_geojson(request, trip_id):
    """Serve the simplified track geometry for the map.

    Tracks are proxied through Django rather than linked by blob URL, so the
    container needs neither public read access nor a CORS rule.
    """
    payload = AzureTrackService().download_track(trip_id, AzureTrackService.GEOJSON_BLOB)

    if payload is None:
        return JsonResponse({'error': 'No track for this trip'}, status=404)

    response = HttpResponse(payload, content_type='application/geo+json')
    response['Cache-Control'] = 'public, max-age=3600'
    return response


def trip_track_download(request, trip_id):
    """Download the original GPX file of a trip."""
    payload = AzureTrackService().download_track(trip_id, AzureTrackService.GPX_BLOB)

    if payload is None:
        return HttpResponse("No track for this trip", status=404)

    response = HttpResponse(payload, content_type='application/gpx+xml')
    response['Content-Disposition'] = f'attachment; filename="{trip_id}.gpx"'
    return response


@admin_required
def trip_track_apply(request, trip_id):
    """Ask before letting a GPX overwrite values the trip already has."""
    service = get_data_service()
    trip = service.get_trip_by_id(trip_id)

    if not trip:
        return HttpResponse("Trip not found", status=404)

    stats = track_stats(trip)
    derived = derived_from_track(stats)
    _, conflicts = compare_with_track(derived, trip)

    if not conflicts:
        # Nothing left to decide - the values match, or the track is gone
        return redirect('trips:trip_edit', trip_id=trip_id)

    if request.method == 'POST':
        chosen = set(request.POST.getlist('fields'))
        updates = {
            conflict['field']: derived[conflict['field']]
            for conflict in conflicts if conflict['field'] in chosen
        }

        if not updates:
            messages.info(request, 'Hodnoty zůstaly beze změny.')
            return redirect('trips:trip_edit', trip_id=trip_id)

        success, message = service.update_trip(trip_id, updates)
        if not success:
            logger.error(f"Error applying track values to {trip_id}: {message}")
            messages.error(request, f'Uložení selhalo: {message}')
            return redirect('trips:trip_edit', trip_id=trip_id)

        labels = [c['label'] for c in conflicts if c['field'] in chosen]
        messages.success(request, 'Přepsáno z GPX: ' + ', '.join(labels) + '.')
        return redirect('trips:trip_edit', trip_id=trip_id)

    return render(request, 'trips/track_apply.html', {
        'trip': trip,
        'conflicts': conflicts,
        'track_stats': stats,
    })


@admin_required
@require_POST
def trip_track_delete(request, trip_id):
    """Remove a trip's GPX track and its statistics."""
    AzureTrackService().delete_track(trip_id)
    success, error = get_data_service().merge_trip_fields(trip_id, {'TrackJson': ''})

    if success:
        messages.success(request, 'Trasa byla smazána.')
    else:
        messages.error(request, f'Trasu se nepodařilo smazat: {error}')

    return redirect('trips:trip_edit', trip_id=trip_id)


# Strava sport types mapped onto the site's own trip classes.
STRAVA_TRIP_CLASS = {
    'Hike': 'Trail',
    'Walk': 'Trail',
    'Snowshoe': 'Trail',
    'Run': 'Run',
    'TrailRun': 'Run',
    'BackcountrySki': 'Skialp',
    'NordicSki': 'Skialp',
    'AlpineSki': 'Slope',
    'Snowboard': 'Slope',
    'RockClimbing': 'Climb',
}


@admin_required
def strava_activities(request):
    """List the connected athlete's recent Strava activities for import."""
    strava = get_strava_service()

    try:
        page = max(1, int(request.GET.get('page', 1)))
    except ValueError:
        page = 1

    context = {
        'strava_configured': strava.is_configured,
        'connected': False,
        'activities': [],
        'trip_id': request.GET.get('trip_id', ''),
        'page': page,
    }

    if strava.is_configured and strava.is_connected(request.user.username):
        context['connected'] = True
        try:
            context['activities'] = strava.list_activities(
                request.user.username, page=context['page']
            )
        except StravaNotConnected as e:
            context['connected'] = False
            messages.warning(request, str(e))
        except StravaError as e:
            messages.error(request, str(e))

    return render(request, 'trips/strava.html', context)


@admin_required
def strava_connect(request):
    """Send the user to Strava to authorize access to their activities."""
    strava = get_strava_service()

    try:
        state = secrets.token_urlsafe(16)
        request.session['strava_oauth_state'] = state
        request.session['strava_oauth_trip_id'] = request.GET.get('trip_id', '')
        redirect_uri = request.build_absolute_uri(reverse('trips:strava_callback'))
        return redirect(strava.authorize_url(redirect_uri, state))
    except StravaNotConfigured as e:
        messages.error(request, str(e))
        return redirect('trips:strava_activities')


@admin_required
def strava_callback(request):
    """Handle Strava's OAuth redirect and store the tokens."""
    strava = get_strava_service()

    expected_state = request.session.pop('strava_oauth_state', None)
    trip_id = request.session.pop('strava_oauth_trip_id', '')

    if request.GET.get('error'):
        messages.error(request, f"Strava odmítla přístup: {request.GET['error']}")
        return redirect('trips:strava_activities')

    if not expected_state or request.GET.get('state') != expected_state:
        messages.error(request, 'Neplatný stav OAuth požadavku, zkus propojení znovu.')
        return redirect('trips:strava_activities')

    code = request.GET.get('code')
    if not code:
        messages.error(request, 'Strava nevrátila autorizační kód.')
        return redirect('trips:strava_activities')

    try:
        tokens = strava.exchange_code(code)
        strava.save_tokens(request.user.username, tokens)
        messages.success(request, 'Účet Strava byl propojen.')
    except (StravaNotConfigured, StravaError) as e:
        messages.error(request, str(e))

    url = reverse('trips:strava_activities')
    return redirect(f"{url}?trip_id={trip_id}" if trip_id else url)


@admin_required
@require_POST
def strava_disconnect(request):
    """Forget the stored Strava tokens."""
    get_strava_service().disconnect(request.user.username)
    messages.success(request, 'Propojení se Stravou bylo zrušeno.')
    return redirect('trips:strava_activities')


@admin_required
@require_POST
def strava_import(request, activity_id):
    """Import a Strava activity's track, into a new trip or an existing one."""
    service = get_data_service()
    strava = StravaService(table_service=service)
    trip_id = request.POST.get('trip_id') or None

    try:
        gpx_bytes, activity = strava.activity_gpx(request.user.username, activity_id)
        parsed = parse_gpx(gpx_bytes)
    except (StravaNotConfigured, StravaNotConnected, StravaError, GpxParseError) as e:
        logger.warning(f"Strava import of activity {activity_id} failed: {str(e)}")
        messages.error(request, str(e))
        return redirect('trips:strava_activities')

    record = track_record(parsed)
    trip_data = {'track_json': json.dumps(record)}
    derived = derived_from_track(record)
    conflicts = []

    if trip_id:
        trip = service.get_trip_by_id(trip_id)
        if not trip:
            messages.error(request, 'Trip nebyl nalezen.')
            return redirect('trips:strava_activities')

        # Same rule as an upload: fill the blanks, ask about the rest
        fills, conflicts = compare_with_track(derived, trip)
        trip_data.update(fills)

        success, message = service.update_trip(trip_id, trip_data)
        if not success:
            messages.error(request, f'Uložení trasy selhalo: {message}')
            return redirect('trips:strava_activities')
    else:
        trip_data['title'] = activity['name'] or f"Strava {activity_id}"
        trip_data['trip_class'] = STRAVA_TRIP_CLASS.get(activity.get('sport_type'), '')
        trip_data.update(derived)
        # A brand new trip has nothing to overwrite, so Strava's own date wins
        if activity.get('start_date'):
            trip_data['trip_completed_on'] = activity['start_date']

        success, message, trip_id = service.create_trip(trip_data)
        if not success:
            messages.error(request, f'Vytvoření tripu selhalo: {message}')
            return redirect('trips:strava_activities')

    success, error = AzureTrackService().upload_track(
        trip_id, gpx_bytes, geojson_bytes(parsed)
    )
    if success:
        messages.success(
            request,
            f"Trasa ze Stravy importována: {parsed['stats']['distance_km']} km, "
            f"↑{parsed['stats']['meters_ascend']} m."
        )
    else:
        messages.error(request, f'Trasu se nepodařilo uložit: {error}')

    if conflicts:
        return redirect('trips:trip_track_apply', trip_id=trip_id)

    return redirect('trips:trip_edit', trip_id=trip_id)


def debug_azure(request):
    """Debug view to test Azure connection"""
    from django.http import JsonResponse
    import json
    
    try:
        # Get the raw connection string from settings
        connection_string = settings.AZURE_STORAGE_CONNECTION_STRING
        print(f"Connection string (first 20 chars): {connection_string[:20]}...")
        
        # Get a specific trip to see all available attributes
        service = get_data_service()
        
        # Get all trips first
        all_trips = service.get_all_trips()
        print(f"Retrieved {len(all_trips)} trips")
        
        if all_trips:
            # Get the first trip's row_key
            first_trip_id = all_trips[0]['row_key']
            print(f"Getting details for trip: {first_trip_id}")
            
            # Get the raw entity from Azure Table
            table_client = service.get_table_client()
            raw_entity = table_client.get_entity(partition_key="Trips", row_key=first_trip_id)
            
            # Convert to a dictionary with all attributes
            trip_data = {}
            for key, value in raw_entity.items():
                if not key.startswith('_') and not key.endswith('@odata.type'):
                    trip_data[key] = str(value)
            
            return JsonResponse({
                'status': 'success',
                'message': 'Azure connection successful',
                'trip_count': len(all_trips),
                'sample_trip_id': first_trip_id,
                'sample_trip_data': trip_data
            })
        else:
            return JsonResponse({
                'status': 'warning',
                'message': 'Azure connection successful but no trips found',
                'trip_count': 0
            })
    except Exception as e:
        import traceback
        return JsonResponse({
            'status': 'error',
            'message': f'Error connecting to Azure: {str(e)}',
            'traceback': traceback.format_exc()
        }, status=500)

def debug_logging(request):
    """Debug view to test logging and check Azure connection"""
    import os
    
    # Check environment variables
    env_conn = os.environ.get("BARUCHSTREKS_STORAGE_CONNECTION")
    env_mapy = os.environ.get("MAPY_CZ_API_KEY")
    
    # Initialize service and try to get trips
    service = get_data_service()
    trips = []
    error_message = None
    
    try:
        trips = service.get_all_trips()
    except Exception as e:
        error_message = str(e)
        logger.error(f"Error in debug_logging: {e}")
    
    # Prepare debug info
    debug_info = {
        'env_conn_exists': env_conn is not None,
        'env_mapy_exists': env_mapy is not None,
        'connection_string_in_service': service.connection_string is not None,
        'trips_count': len(trips),
        'error_message': error_message,
        'trips': trips[:3] if trips else []  # Show first 3 trips for debugging
    }
    
    return render(request, 'trips/debug.html', {
        'debug_info': debug_info
    })
