#!/bin/sh
# Migrates the SQLite auth database, creates the superuser and starts the CMD.
set -e

python manage.py migrate --noinput

python manage.py shell << 'PY'
import os
from django.contrib.auth import get_user_model

User = get_user_model()
username = os.environ.get('DJANGO_SUPERUSER_USERNAME', 'austy')
password = os.environ.get('DJANGO_SUPERUSER_PASSWORD')

if password and not User.objects.filter(username=username).exists():
    User.objects.create_superuser(username=username, email='', password=password)
    print(f'Superuser {username} created')
else:
    print(f'Superuser {username} already exists or no password set')
PY

exec "$@"
