FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    DJANGO_DB_PATH=/data/db.sqlite3

WORKDIR /app

COPY requirements.txt .
RUN pip install -r requirements.txt

COPY . .
RUN python manage.py collectstatic --noinput

# /data is the volume with SQLite (only the admin account and sessions; trips live in Azure).
# A new named volume inherits the owner from the image, otherwise it would be root's.
RUN useradd --system --create-home --uid 10001 app \
    && mkdir -p /data \
    && chown -R app /data \
    && chmod +x docker-entrypoint.sh
USER app

EXPOSE 8000

ENTRYPOINT ["./docker-entrypoint.sh"]
CMD ["gunicorn", "--bind=0.0.0.0:8000", "--workers", "2", "--threads", "4", "--timeout", "600", "--access-logfile", "-", "--error-logfile", "-", "baruchstreks.wsgi:application"]
