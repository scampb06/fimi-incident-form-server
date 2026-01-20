# GET /google-sheets/check-permissions

## Overview

Checks whether the configured service account has read (and optionally write) permissions to access a Google Sheet. This endpoint is useful for validating service account setup before attempting data operations. It performs lightweight permission tests by reading spreadsheet metadata and optionally testing write access.

## HTTP Method

`GET`

## Parameters

### Query Parameters

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `url` | string | Yes | - | Full Google Sheets URL |
| `checkWrite` | boolean | No | `false` | If `true`, checks for write permissions; if `false`, checks only read permissions |

### Example Requests

```
GET /google-sheets/check-permissions?url=https://docs.google.com/spreadsheets/d/1ABC123/edit#gid=0
GET /google-sheets/check-permissions?url=https://docs.google.com/spreadsheets/d/1ABC123/edit#gid=0&checkWrite=true
```

## Response

### Success Response (200 OK) - Has Permission

```json
{
  "hasPermission": true,
  "permissionType": "read",
  "message": "Service account has read permission to the Google Sheet",
  "spreadsheetId": "1ABC123xyz",
  "sheetName": "Sheet1",
  "gid": 0,
  "serviceAccount": "service-account@project.iam.gserviceaccount.com",
  "debug": {
    "canReadMetadata": true,
    "canReadData": true,
    "canWrite": null
  }
}
```

**With Write Check:**
```json
{
  "hasPermission": true,
  "permissionType": "write",
  "message": "Service account has write permission to the Google Sheet",
  "spreadsheetId": "1ABC123xyz",
  "sheetName": "Sheet1",
  "gid": 0,
  "serviceAccount": "service-account@project.iam.gserviceaccount.com",
  "debug": {
    "canReadMetadata": true,
    "canReadData": true,
    "canWrite": true
  }
}
```

### No Permission Response (200 OK)

```json
{
  "hasPermission": false,
  "permissionType": "none",
  "message": "Service account does not have access to this Google Sheet",
  "spreadsheetId": "1ABC123xyz",
  "gid": 0,
  "serviceAccount": "service-account@project.iam.gserviceaccount.com",
  "error": "permission_denied",
  "googleError": "The caller does not have permission",
  "instructions": {
    "step1": "Open the Google Sheet in your browser",
    "step2": "Click the 'Share' button",
    "step3": "Add this email address: service-account@project.iam.gserviceaccount.com",
    "step4": "Set permission to 'Viewer' for read access or 'Editor' for write access"
  }
}
```

### Error Responses

**400 Bad Request** - Missing URL parameter
```json
{
  "hasPermission": false,
  "message": "URL parameter is required"
}
```

**400 Bad Request** - Invalid Google Sheets URL
```json
{
  "hasPermission": false,
  "message": "Invalid Google Sheets URL. Expected format: https://docs.google.com/spreadsheets/d/{SPREADSHEET_ID}/edit#gid={SHEET_ID}",
  "providedUrl": "invalid-url"
}
```

**400 Bad Request** - Service account not configured
```json
{
  "hasPermission": false,
  "message": "Service account configuration is missing",
  "debug": {
    "hasClientEmail": false,
    "hasPrivateKey": false,
    "hasTokenUri": true,
    "privateKeyLength": 0
  }
}
```

**401 Unauthorized** - Authentication failed
```json
{
  "hasPermission": false,
  "message": "Failed to authenticate with Google Sheets API - check service account credentials in App Settings",
  "error": "authentication_failed",
  "debug": {
    "clientEmail": "service-account@project.iam.gserviceaccount.com",
    "privateKeyLength": 1704,
    "hasTokenUri": true
  }
}
```

## Permission Checks Performed

The endpoint performs the following validation steps:

### For Read Permission Check:
1. **Metadata Access**: Attempts to read spreadsheet metadata
2. **Data Access**: Attempts to read the first row (headers) of the sheet

### For Write Permission Check:
1. All read checks above
2. **Write Test**: Attempts to append a test value to verify write access

## Configuration Requirements

The following application settings must be configured:

- `GoogleSheets:ServiceAccount:client_email`: Service account email
- `GoogleSheets:ServiceAccount:private_key`: Service account private key (PEM format)
- `GoogleSheets:ServiceAccount:token_uri`: Google OAuth token URI

## Example Usage

```bash
# Check read permission
curl "https://your-app.azurewebsites.net/google-sheets/check-permissions?url=https%3A%2F%2Fdocs.google.com%2Fspreadsheets%2Fd%2F1ABC123%2Fedit%23gid%3D0"

# Check write permission
curl "https://your-app.azurewebsites.net/google-sheets/check-permissions?url=https%3A%2F%2Fdocs.google.com%2Fspreadsheets%2Fd%2F1ABC123%2Fedit%23gid%3D0&checkWrite=true"
```

```javascript
// JavaScript fetch example
async function checkPermissions(sheetUrl, checkWrite = false) {
  const encodedUrl = encodeURIComponent(sheetUrl);
  const url = `/google-sheets/check-permissions?url=${encodedUrl}&checkWrite=${checkWrite}`;
  
  const response = await fetch(url);
  const result = await response.json();
  
  if (result.hasPermission) {
    console.log(`✓ Service account has ${result.permissionType} permission`);
  } else {
    console.error(`✗ ${result.message}`);
    if (result.instructions) {
      console.log('Setup instructions:', result.instructions);
    }
  }
  
  return result;
}

// Usage
await checkPermissions('https://docs.google.com/spreadsheets/d/1ABC123/edit#gid=0');
await checkPermissions('https://docs.google.com/spreadsheets/d/1ABC123/edit#gid=0', true);
```

## Common Issues

### "Service account does not have access"
**Solution**: Share the Google Sheet with the service account email address
1. Open the Google Sheet
2. Click "Share" button
3. Add: `service-account@project-id.iam.gserviceaccount.com`
4. Grant "Viewer" (read) or "Editor" (write) permission

### "Failed to authenticate"
**Solution**: Verify service account credentials in app settings
- Check that `private_key` includes `-----BEGIN PRIVATE KEY-----` header
- Ensure `client_email` matches the service account
- Verify `token_uri` is `https://oauth2.googleapis.com/token`

## Notes

- Read permission requires at least "Viewer" role on the Google Sheet
- Write permission requires "Editor" role
- The endpoint uses different OAuth scopes based on `checkWrite` parameter:
  - Read: `https://www.googleapis.com/auth/spreadsheets.readonly`
  - Write: `https://www.googleapis.com/auth/spreadsheets`
- Permission checks are lightweight and do not modify sheet data
- Debug information is included to help troubleshoot configuration issues
