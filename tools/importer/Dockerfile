FROM python:3.12-slim
WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY import_gq7.py entry.sh ./
ENTRYPOINT ["/app/entry.sh"]
