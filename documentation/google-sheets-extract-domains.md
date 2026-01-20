# POST /google-sheets/extract-domains

## Overview

Extracts and populates the top-level domain for each URL in a Google Sheet. The endpoint reads URLs from the sheet, extracts their domains (e.g., `twitter.com` from `https://twitter.com/user/status/123`), and writes the results back to a "Domain" column. This is useful for categorizing and analyzing URLs by their source domain.

## HTTP Method

`POST`

## Parameters

### Query Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `url` | string | Yes | Full Google Sheets URL |

### Example Request

```
POST /google-sheets/extract-domains?url=https://docs.google.com/spreadsheets/d/1ABC123/edit#gid=0
```

## Response

### Success Response (200 OK)

```json
{
  "message": "Successfully extracted domains for 50 URLs",
  "totalRecords": 100,
  "processedCount": 50,
  "skippedCount": 50,
  "spreadsheetId": "1ABC123xyz",
  "sheetName": "Sheet1",
  "sourceUrl": "https://docs.google.com/spreadsheets/d/1ABC123xyz/edit#gid=0",
  "note": "Domains extracted from URL column and written to Domain column. Skipped rows that already had domains or archived URLs."
}
```

**No URLs to Process:**
```json
{
  "message": "No new URLs found to process",
  "totalRecords": 10,
  "processedCount": 0
}
```

### Error Responses

**400 Bad Request** - Missing URL parameter
```json
{
  "message": "URL parameter is required"
}
```

**400 Bad Request** - Invalid URL format
```json
{
  "message": "Invalid Google Sheets URL. Expected format: https://docs.google.com/spreadsheets/d/{SPREADSHEET_ID}/edit#gid={SHEET_ID}",
  "providedUrl": "invalid-url"
}
```

**400 Bad Request** - No URL column found
```json
{
  "message": "No URL column found in the sheet",
  "headers": ["Title", "Description", "Date"]
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

## Sheet Requirements

### Required Columns

The Google Sheet must have these columns (case-insensitive):

- **URL**: Contains the URLs to process
- **Domain** (optional): Will be created if it doesn't exist

### Optional Columns

- **Archive URL**: If present, rows with archive URLs will be skipped (already processed)

### Example Sheet Structure

**Before:**
```
| URL                                  | Domain | Archive URL |
|--------------------------------------|--------|-------------|
| https://twitter.com/user/status/123  |        |             |
| https://facebook.com/post/456        |        |             |
| https://youtube.com/watch?v=abc      | youtube.com | https://web.archive.org/... |
```

**After:**
```
| URL                                  | Domain      | Archive URL |
|--------------------------------------|-------------|-------------|
| https://twitter.com/user/status/123  | twitter.com |             |
| https://facebook.com/post/456        | facebook.com|             |
| https://youtube.com/watch?v=abc      | youtube.com | https://web.archive.org/... |
```

## Domain Extraction Logic

The endpoint extracts domains using the following rules:

1. **Parse URL**: Extracts the host from the URL
2. **Remove Subdomains**: Removes common subdomains (www, m, mobile, etc.)
3. **Extract Top-Level Domain**: Returns the main domain (e.g., `example.com`)

### Examples:

| Input URL | Extracted Domain |
|-----------|------------------|
| `https://www.twitter.com/user` | `twitter.com` |
| `https://m.facebook.com/page` | `facebook.com` |
| `https://subdomain.example.com/path` | `example.com` |
| `https://docs.google.com/document` | `google.com` |

## Processing Rules

The endpoint processes rows based on these conditions:

✅ **Processed:**
- Row has a URL
- Domain column is empty
- No Archive URL (not yet archived)

⏭️ **Skipped:**
- Row already has a domain value
- Row has an Archive URL (already processed)
- URL column is empty

## Configuration Requirements

The following application settings must be configured:

- `GoogleSheets:ServiceAccount:client_email`: Service account email
- `GoogleSheets:ServiceAccount:private_key`: Service account private key
- `GoogleSheets:ServiceAccount:token_uri`: Google OAuth token URI

**Required Permission:** Service account must have **Editor** access to the Google Sheet.

## Example Usage

```bash
curl -X POST "https://your-app.azurewebsites.net/google-sheets/extract-domains?url=https%3A%2F%2Fdocs.google.com%2Fspreadsheets%2Fd%2F1ABC123%2Fedit%23gid%3D0"
```

```javascript
// JavaScript fetch example
async function extractDomains(sheetUrl) {
  const encodedUrl = encodeURIComponent(sheetUrl);
  
  const response = await fetch(`/google-sheets/extract-domains?url=${encodedUrl}`, {
    method: 'POST'
  });
  
  const result = await response.json();
  console.log(`Processed ${result.processedCount} URLs`);
  console.log(`Skipped ${result.skippedCount} already-processed rows`);
  
  return result;
}

// Usage
await extractDomains('https://docs.google.com/spreadsheets/d/1ABC123/edit#gid=0');
```

## Batch Update

The endpoint uses the Google Sheets Batch Update API to efficiently write all domains in a single request:

- All updates are sent in one batch operation
- Maintains data consistency
- Reduces API quota usage

## Notes

- The endpoint requires **write permissions** (Editor role) on the Google Sheet
- Domain extraction handles malformed URLs gracefully
- Rows are processed in order
- The first row is treated as headers
- Empty cells in the Domain column will be filled; existing values are preserved
- Archive URL column is used to identify already-processed rows
- Processing is idempotent - running multiple times won't duplicate work
