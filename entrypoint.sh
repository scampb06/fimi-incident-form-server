#!/bin/bash
set -e

# Create secrets directory if it doesn't exist
mkdir -p /mnt/fileshare/secrets

# Generate service_account.json from environment variables using Python for proper JSON escaping
python3 << 'PYTHON_EOF' > /mnt/fileshare/secrets/service_account.json
import json
import os

service_account = {
    "type": "service_account",
    "project_id": os.environ.get("GOOGLE_PROJECT_ID", ""),
    "private_key_id": os.environ.get("GOOGLE_PRIVATE_KEY_ID", ""),
    "private_key": os.environ.get("GOOGLE_PRIVATE_KEY", ""),
    "client_email": os.environ.get("GOOGLE_CLIENT_EMAIL", ""),
    "client_id": os.environ.get("GOOGLE_CLIENT_ID", ""),
    "auth_uri": "https://accounts.google.com/o/oauth2/auth",
    "token_uri": "https://oauth2.googleapis.com/token",
    "auth_provider_x509_cert_url": "https://www.googleapis.com/oauth2/v1/certs",
    "client_x509_cert_url": os.environ.get("GOOGLE_CLIENT_CERT_URL", ""),
    "universe_domain": "googleapis.com"
}

print(json.dumps(service_account, indent=2))
PYTHON_EOF

echo "Service account file created at /mnt/fileshare/secrets/service_account.json"

# Run auto-archiver with passed arguments
# Use python3 -m auto_archiver (the bellingcat image's original entrypoint)
exec python3 -m auto_archiver "$@"
