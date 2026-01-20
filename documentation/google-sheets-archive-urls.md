# GET/POST /google-sheets/archive-urls

## Overview

Archives URLs in a Google Sheet using the Wayback Machine (Internet Archive). The endpoint processes URLs in parallel batches, submits them to the Wayback Machine for archiving, and writes the archive URLs and status back to the sheet. It includes retry logic, pre-validation options, and batch processing for efficient handling of large URL lists.

## HTTP Methods

`GET` or `POST`

## Parameters

### Query Parameters

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `url` | string | Yes | - | Full Google Sheets URL |
| `preValidation` | boolean | No | `false` | If `true`, validates URLs before archiving to skip invalid/unreachable URLs |

### Example Requests

```
GET /google-sheets/archive-urls?url=https://docs.google.com/spreadsheets/d/1ABC123/edit#gid=0
POST /google-sheets/archive-urls?url=https://docs.google.com/spreadsheets/d/1ABC123/edit#gid=0&preValidation=true
```

## Response

### Success Response (200 OK)

```json
{
  "message": "Successfully processed 50 URLs and archived 45 of them",
  "totalRecords": 100,
  "processedCount": 50,
  "archivedCount": 45,
  "failedCount": 5,
  "skippedCount": 50,
  "successRate": 90.0,
  "estimatedTimePerUrl": "~2 seconds",
  "spreadsheetId": "1ABC123xyz",
  "sheetName": "Sheet1",
  "sourceUrl": "https://docs.google.com/spreadsheets/d/1ABC123xyz/edit#gid=0",
  "columnsUpdated": ["Archive Status", "Archive URL"],
  "note": "Archive Status shows 'Success' or error messages in red. Archive URL contains the archived link for successful archives."
}
```

**No URLs to Process:**
```json
{
  "message": "No new URLs found to archive",
  "totalRecords": 10,
  "processedCount": 0,
  "archivedCount": 0
}
```

**No Data:**
```json
{
  "message": "No data found in the sheet",
  "totalRecords": 0,
  "archivedCount": 0
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

**401 Unauthorized** - Authentication failed
```json
{
  "type": "about:blank",
  "title": "One or more errors occurred.",
  "status": 401,
  "detail": "Failed to authenticate with Google Sheets API"
}
```

**500 Internal Server Error** - Update failed
```json
{
  "type": "about:blank",
  "title": "One or more errors occurred.",
  "status": 500,
  "detail": "Failed to update Google Sheet with archive results"
}
```

## Sheet Requirements

### Required Columns

The Google Sheet must have these columns (case-insensitive):

- **URL**: Contains the URLs to archive

### Optional Columns

These columns will be created if they don't exist:

- **Archive Status**: Status of the archiving operation (`Success` or error message in red)
- **Archive URL**: The Wayback Machine archive URL (e.g., `https://web.archive.org/web/20240115123456/https://example.com`)

### Example Sheet Structure

**Before:**
```
| URL                     | Archive Status | Archive URL |
|-------------------------|----------------|-------------|
| https://example.com     |                |             |
| https://test.com        |                |             |
| https://invalid-url     |                |             |
```

**After (with preValidation=false):**
```
| URL                     | Archive Status        | Archive URL |
|-------------------------|-----------------------|-------------|
| https://example.com     | Success               | https://web.archive.org/web/20240115123456/https://example.com |
| https://test.com        | Success               | https://web.archive.org/web/20240115234567/https://test.com |
| https://invalid-url     | ERROR: Invalid URL    |             |
```

**After (with preValidation=true):**
```
| URL                     | Archive Status                    | Archive URL |
|-------------------------|-----------------------------------|-------------|
| https://example.com     | Success                           | https://web.archive.org/web/20240115123456/https://example.com |
| https://test.com        | Success                           | https://web.archive.org/web/20240115234567/https://test.com |
| https://invalid-url     | ERROR: Pre-validation failed      |             |
```

## Processing Features

### Parallel Batch Processing

The endpoint uses configurable batch processing:

- **Batch Size**: Default 10 URLs per batch (configurable via `ArchiveUrlsBatchSize`)
- **Max Concurrency**: Default 5 parallel batches (configurable via `ArchiveUrlsMaxConcurrency`)
- **Total Throughput**: Up to 50 URLs processed simultaneously

### Pre-Validation (Optional)

When `preValidation=true`:

1. **URL Format Check**: Validates URL structure
2. **DNS Resolution**: Checks if domain exists
3. **HTTP HEAD Request**: Verifies URL is reachable
4. **Skip Invalid URLs**: URLs that fail validation are marked as errors without attempting to archive

Benefits:
- Saves Wayback Machine API quota
- Faster processing (skips unreachable URLs)
- Clearer error messages

### Retry Logic

For each URL, the endpoint attempts:

1. **Initial Archive Request**
2. **Up to 2 Retries** on failure
3. **Exponential Backoff** between retries (2s, 4s)

### Error Handling

Archive Status values:

- **Success**: URL successfully archived
- **ERROR: {message}**: Specific error (displayed in red in Google Sheets)

Common error messages:
- `Invalid URL`
- `Pre-validation failed`
- `Wayback Machine error`
- `Network error`
- `Timeout`

## Processing Rules

✅ **Processed:**
- Row has a valid URL
- Archive Status column is empty
- Archive URL column is empty

⏭️ **Skipped:**
- Row already has Archive Status value
- Row already has Archive URL (previously archived)
- URL column is empty

## Configuration Requirements

### Required Application Settings

- `GoogleSheets:ServiceAccount:client_email`: Service account email
- `GoogleSheets:ServiceAccount:private_key`: Service account private key
- `GoogleSheets:ServiceAccount:token_uri`: Google OAuth token URI

### Optional Application Settings

- `ArchiveUrlsBatchSize`: Number of URLs per batch (default: 10)
- `ArchiveUrlsMaxConcurrency`: Max parallel batches (default: 5)

**Required Permission:** Service account must have **Editor** access to the Google Sheet.

## Example Usage

```bash
# Archive without pre-validation
curl -X POST "https://your-app.azurewebsites.net/google-sheets/archive-urls?url=https%3A%2F%2Fdocs.google.com%2Fspreadsheets%2Fd%2F1ABC123%2Fedit%23gid%3D0"

# Archive with pre-validation
curl -X POST "https://your-app.azurewebsites.net/google-sheets/archive-urls?url=https%3A%2F%2Fdocs.google.com%2Fspreadsheets%2Fd%2F1ABC123%2Fedit%23gid%3D0&preValidation=true"
```

```javascript
// JavaScript fetch example
async function archiveUrls(sheetUrl, preValidation = false) {
  const encodedUrl = encodeURIComponent(sheetUrl);
  const url = `/google-sheets/archive-urls?url=${encodedUrl}&preValidation=${preValidation}`;
  
  const response = await fetch(url, { method: 'POST' });
  const result = await response.json();
  
  console.log(`✓ Archived: ${result.archivedCount}/${result.processedCount}`);
  console.log(`✗ Failed: ${result.failedCount}`);
  console.log(`⏭ Skipped: ${result.skippedCount}`);
  console.log(`Success Rate: ${result.successRate}%`);
  
  return result;
}

// Usage
await archiveUrls('https://docs.google.com/spreadsheets/d/1ABC123/edit#gid=0', true);
```

## Performance Estimates

- **Average Time per URL**: ~2 seconds
- **100 URLs**: ~4-5 minutes (with default batch settings)
- **500 URLs**: ~20-25 minutes

## Notes

- The endpoint uses the Wayback Machine Save Page Now API
- Wayback Machine may take longer for complex pages (videos, dynamic content)
- Some URLs may not be archivable (robots.txt restrictions, authentication required, etc.)
- Archive Status is written in red text for errors (using Google Sheets text formatting)
- The endpoint is idempotent - running multiple times won't re-archive already processed URLs
- Pre-validation adds ~0.5-1 second per URL but can save significant time by skipping invalid URLs
- Batch processing continues even if individual URLs fail
- Progress is saved incrementally (each batch update writes to the sheet)
