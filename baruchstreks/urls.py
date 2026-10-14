"""
URL configuration for baruchstreks project.

The `urlpatterns` list routes URLs to views. For more information please see:
    https://docs.djangoproject.com/en/5.1/topics/http/urls/
Examples:
Function views
    1. Add an import:  from my_app import views
    2. Add a URL to urlpatterns:  path('', views.home, name='home')
Class-based views
    1. Add an import:  from other_app.views import Home
    2. Add a URL to urlpatterns:  path('', Home.as_view(), name='home')
Including another URLconf
    1. Import the include() function: from django.urls import include, path
    2. Add a URL to urlpatterns:  path('blog/', include('blog.urls'))
"""
from django.contrib import admin
from django.http import JsonResponse
from django.urls import path, include
from django.views.generic import RedirectView
from django.contrib.auth import views as auth_views
from django.conf import settings
from django.views.defaults import server_error
from trips.auth_views import custom_login

# Custom error handler view
def custom_server_error(request, *args, **kwargs):
    """Custom 500 error handler that logs the error"""
    import logging
    import sys
    import traceback
    
    logger = logging.getLogger('django.request')
    exc_info = sys.exc_info()
    
    if exc_info and exc_info[0]:
        logger.error(
            'Internal Server Error: %s', request.path,
            exc_info=exc_info,
            extra={'status_code': 500, 'request': request}
        )
        
        # Log the full traceback for easier debugging
        logger.error('Traceback: %s', traceback.format_exc())
    
    # Call the default server_error view
    return server_error(request, *args, **kwargs)

urlpatterns = [
    path('healthz', lambda request: JsonResponse({'status': 'ok'}), name='healthz'),
    path('admin/', admin.site.urls),
    path('trips/', include('trips.urls')),
    path('', RedirectView.as_view(url='/trips/', permanent=False)),
    
    # Authentication URLs
    path('accounts/login/', custom_login, name='login'),
    path('accounts/logout/', auth_views.LogoutView.as_view(template_name='auth/logged_out.html'), name='logout'),
]

# Register custom error handlers
if not settings.DEBUG:
    # Only use custom handlers in production
    handler500 = custom_server_error
