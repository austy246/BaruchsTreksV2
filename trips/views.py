from django.shortcuts import render, redirect
from django.contrib import messages
from django.conf import settings
import json
import traceback
import logging
from datetime import datetime, timezone
from django.http import HttpResponse, HttpResponseServerError
from django.contrib.auth.decorators import login_required, user_passes_test
from django.urls import reverse

from .azure_service import AzureTableService
from .blob_service import AzureBlobService
from .forms import TripEditForm

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
        'trip_photos': trip_photos
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
                        })
                
                # Handle photo uploads
                if request.FILES.getlist('photos'):
                    for photo_file in request.FILES.getlist('photos'):
                        success, result = blob_service.upload_photo(trip_id, photo_file)
                        if not success:
                            logger.warning(f"Error uploading photo: {result}")
                
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
        }
        
        if not is_new_trip:
            context.update({
                'trip': trip,
                'trip_photos': trip_photos,
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
