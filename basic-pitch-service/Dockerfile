FROM python:3.10-slim

ENV PYTHONUNBUFFERED=1 PIP_NO_CACHE_DIR=1

RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg libsndfile1 \
    && rm -rf /var/lib/apt/lists/* \
    # resampy used by Basic Pitch 0.4.0 still imports pkg_resources, which was
    # removed from newer setuptools releases.
    && pip install --upgrade pip 'setuptools==80.9.0' wheel \
    && pip install 'numpy==1.26.4' basic-pitch==0.4.0 fastapi==0.116.1 'uvicorn[standard]==0.35.0' python-multipart==0.0.20

WORKDIR /service
COPY app.py .

EXPOSE 8090
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8090/health', timeout=3)"
CMD ["python", "-m", "uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8090"]
