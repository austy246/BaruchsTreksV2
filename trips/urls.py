from django.urls import path
from . import views

app_name = 'trips'

urlpatterns = [
    path('', views.index, name='index'),
    path('all/', views.all_trips, name='all_trips'),
    path('trip/new/', views.trip_edit, name='trip_create'),
    path('trip/<str:trip_id>/', views.trip_detail, name='trip_detail'),
    path('trip/<str:trip_id>/edit/', views.trip_edit, name='trip_edit'),
    path('trip/<str:trip_id>/copy/', views.trip_copy, name='trip_copy'),
    path('trip/<str:trip_id>/delete/', views.trip_delete, name='trip_delete'),
    path('trip/<str:trip_id>/track.geojson', views.trip_track_geojson, name='trip_track_geojson'),
    path('trip/<str:trip_id>/track.gpx', views.trip_track_download, name='trip_track_download'),
    path('trip/<str:trip_id>/track/apply/', views.trip_track_apply, name='trip_track_apply'),
    path('trip/<str:trip_id>/track/delete/', views.trip_track_delete, name='trip_track_delete'),
    path('strava/', views.strava_activities, name='strava_activities'),
    path('strava/connect/', views.strava_connect, name='strava_connect'),
    path('strava/callback/', views.strava_callback, name='strava_callback'),
    path('strava/disconnect/', views.strava_disconnect, name='strava_disconnect'),
    path('strava/import/<str:activity_id>/', views.strava_import, name='strava_import'),
    path('debug/', views.debug_azure, name='debug_azure'),
    path('debug-logging/', views.debug_logging, name='debug_logging'),
]
