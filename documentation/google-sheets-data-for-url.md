# GET /google-sheets/data-for-url

## Overview

Retrieves all data from a Google Sheet using a service account for authentication. The endpoint automatically extracts the spreadsheet ID and sheet ID (GID) from a Google Sheets URL, identifies the correct worksheet, and returns all cell data in a structured JSON format.

## HTTP Method

`GET`

## Parameters

### Query Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `url` | string | Yes | Full Google Sheets URL (e.g., `https://docs.google.com/spreadsheets/d/{SPREADSHEET_ID}/edit#gid={SHEET_ID}`) |

### Example Request

```
GET /google-sheets/data-for-url?url=https://docs.google.com/spreadsheets/d/1ABC123xyz/edit#gid=0
```

## Response

### Success Response (200 OK)

Returns the sheet data as an array of records with column headers as keys.

```json
{
  "data": [
    {
      "URL": "https://example.com",
      "Title": "Example Title",
      "Status": "Active",
      "Date": "2024-01-15"
    },
    {
      "URL": "https://another-example.com",
      "Title": "Another Title",
      "Status": "Pending",
      "Date": "2024-01-16"
    }
  ],
  "count": 2,
  "method": "Service Account API",
  "spreadsheetId": "1ABC123xyz",
  "sheetName": "Sheet1",
  "gid": 0,
  "sourceUrl": "https://docs.google.com/spreadsheets/d/1ABC123xyz/edit#gid=0"
}
```

### Error Responses

**400 Bad Request** - Missing URL parameter
```json
{
  "message": "URL parameter is required"
}
```

**400 Bad Request** - Invalid Google Sheets URL
```json
{
  "message": "Invalid Google Sheets URL. Expected format: https://docs.google.com/spreadsheets/d/{SPREADSHEET_ID}/edit#gid={SHEET_ID}",
  "providedUrl": "invalid-url"
}
```

**400 Bad Request** - Service account not configured
```json
{
  "message": "Service account configuration is missing"
}
```

**401 Unauthorized** - Authentication failed
```json
{
  "type": "about:blank",
  "title": "One or more errors occurred.",
  "status": 401,
  "detail": "Failed to authenticate with Google Sheets API"
}
```

**Google Sheets API Error** - Error from Google
```json
{
  "type": "about:blank",
  "title": "One or more errors occurred.",
  "status": 403,
  "detail": "Google Sheets API error: {error details}"
}
```

## Configuration Requirements

The following application settings must be configured:

- `GoogleSheets:ServiceAccount:client_email`: Service account email address
- `GoogleSheets:ServiceAccount:private_key`: Service account private key (PEM format)
- `GoogleSheets:ServiceAccount:token_uri`: Google OAuth token URI (typically `https://oauth2.googleapis.com/token`)

## URL Format

The endpoint accepts standard Google Sheets URLs in these formats:

```
https://docs.google.com/spreadsheets/d/{SPREADSHEET_ID}/edit#gid={SHEET_ID}
https://docs.google.com/spreadsheets/d/{SPREADSHEET_ID}/edit?gid={SHEET_ID}
https://docs.google.com/spreadsheets/d/{SPREADSHEET_ID}/edit
```

- If no GID is specified, the first sheet in the spreadsheet is used
- The GID can be in the fragment (`#gid=0`) or query parameter (`?gid=0`)

## Data Format

The response converts sheet data from rows/columns into an array of objects:

**Sheet Data:**
```
| URL                  | Title         | Status  |
|---------------------|---------------|---------|
| https://example.com | Example Title | Active  |
| https://test.com    | Test Title    | Pending |
```

**Converted Response:**
```json
{
  "data": [
    {"URL": "https://example.com", "Title": "Example Title", "Status": "Active"},
    {"URL": "https://test.com", "Title": "Test Title", "Status": "Pending"}
  ],
  "count": 2
}
```

## Example Usage

```bash
curl "https://your-app.azurewebsites.net/google-sheets/data-for-url?url=https%3A%2F%2Fdocs.google.com%2Fspreadsheets%2Fd%2F1ABC123%2Fedit%23gid%3D0"
```

```javascript
// JavaScript fetch example
const sheetUrl = 'https://docs.google.com/spreadsheets/d/1ABC123xyz/edit#gid=0';
const encodedUrl = encodeURIComponent(sheetUrl);

const response = await fetch(`/google-sheets/data-for-url?url=${encodedUrl}`);
const result = await response.json();

console.log(`Retrieved ${result.count} records from ${result.sheetName}`);
result.data.forEach(row => {
  console.log(row);
});
```

## Prerequisites

1. **Create Google Service Account:**
   - Go to [Google Cloud Console](https://console.cloud.google.com)
   - Create a project and enable Google Sheets API
   - Create a service account and download JSON key

2. **Share Google Sheet:**
   - Share your Google Sheet with the service account email address
   - Grant at least "Viewer" permission

3. **Configure App Settings:**
   - Add service account credentials to application configuration
   - See [appsettings.md](../appsettings.md) for details

## Notes

- The endpoint uses Google Sheets API v4
- Service account authentication generates a JWT token for each request
- The first row of the sheet is treated as column headers
- Empty cells are returned as empty strings
- Maximum sheet size is limited by Google Sheets (10 million cells)
- API quotas apply (100 requests per 100 seconds per user by default)
