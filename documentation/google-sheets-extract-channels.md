# POST /google-sheets/extract-channels

## Overview

Extracts and populates the channel/account name for each URL in a Google Sheet. The endpoint analyzes URLs from social media platforms (Twitter, Facebook, YouTube, TikTok, Instagram, Telegram) and extracts the channel, username, or account identifier, writing the results back to a "Channel" column.

## HTTP Method

`POST`

## Parameters

### Query Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `url` | string | Yes | Full Google Sheets URL |

### Example Request

```
POST /google-sheets/extract-channels?url=https://docs.google.com/spreadsheets/d/1ABC123/edit#gid=0
```

## Response

### Success Response (200 OK)

```json
{
  "message": "Successfully extracted channels for 45 URLs",
  "totalRecords": 100,
  "processedCount": 45,
  "skippedCount": 55,
  "spreadsheetId": "1ABC123xyz",
  "sheetName": "Sheet1",
  "sourceUrl": "https://docs.google.com/spreadsheets/d/1ABC123xyz/edit#gid=0",
  "note": "Channels extracted from URL column and written to Channel column. Skipped rows that already had channels or archived URLs."
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
- **Channel** (optional): Will be created if it doesn't exist

### Optional Columns

- **Archive URL**: If present, rows with archive URLs will be skipped (already processed)

### Example Sheet Structure

**Before:**
```
| URL                                       | Channel | Archive URL |
|-------------------------------------------|---------|-------------|
| https://twitter.com/johndoe/status/123    |         |             |
| https://youtube.com/@ChannelName/videos   |         |             |
| https://t.me/channel_name                 |         |             |
```

**After:**
```
| URL                                       | Channel      | Archive URL |
|-------------------------------------------|--------------|-------------|
| https://twitter.com/johndoe/status/123    | johndoe      |             |
| https://youtube.com/@ChannelName/videos   | ChannelName  |             |
| https://t.me/channel_name                 | channel_name |             |
```

## Channel Extraction Logic

The endpoint uses platform-specific extraction patterns:

### Supported Platforms

| Platform | Example URL | Extracted Channel |
|----------|-------------|-------------------|
| **Twitter/X** | `twitter.com/username/status/123` | `username` |
| **Facebook** | `facebook.com/username/posts/456` | `username` |
| **YouTube** | `youtube.com/@ChannelName/videos` | `ChannelName` |
| **YouTube** | `youtube.com/channel/UCxxx123` | `UCxxx123` |
| **YouTube** | `youtube.com/c/CustomName` | `CustomName` |
| **TikTok** | `tiktok.com/@username/video/123` | `username` |
| **Instagram** | `instagram.com/username/p/abc123` | `username` |
| **Telegram** | `t.me/channel_name` | `channel_name` |

### Extraction Patterns

1. **Twitter/X**: Extracts username from path (first segment after domain)
2. **Facebook**: Extracts username or page name
3. **YouTube**: Handles `@handle`, `/channel/ID`, `/c/CustomName`, `/user/Name` formats
4. **TikTok**: Extracts `@username`
5. **Instagram**: Extracts username from path
6. **Telegram**: Extracts channel name from `t.me/channelname`

## Processing Rules

The endpoint processes rows based on these conditions:

✅ **Processed:**
- Row has a URL
- Channel column is empty
- No Archive URL (not yet archived)
- URL is from a supported platform

⏭️ **Skipped:**
- Row already has a channel value
- Row has an Archive URL (already processed)
- URL column is empty
- URL is from an unsupported platform

## Configuration Requirements

The following application settings must be configured:

- `GoogleSheets:ServiceAccount:client_email`: Service account email
- `GoogleSheets:ServiceAccount:private_key`: Service account private key
- `GoogleSheets:ServiceAccount:token_uri`: Google OAuth token URI

**Required Permission:** Service account must have **Editor** access to the Google Sheet.

## Example Usage

```bash
curl -X POST "https://your-app.azurewebsites.net/google-sheets/extract-channels?url=https%3A%2F%2Fdocs.google.com%2Fspreadsheets%2Fd%2F1ABC123%2Fedit%23gid%3D0"
```

```javascript
// JavaScript fetch example
async function extractChannels(sheetUrl) {
  const encodedUrl = encodeURIComponent(sheetUrl);
  
  const response = await fetch(`/google-sheets/extract-channels?url=${encodedUrl}`, {
    method: 'POST'
  });
  
  const result = await response.json();
  console.log(`Processed ${result.processedCount} URLs`);
  console.log(`Skipped ${result.skippedCount} already-processed rows`);
  
  return result;
}

// Usage
await extractChannels('https://docs.google.com/spreadsheets/d/1ABC123/edit#gid=0');
```

## Handling Unsupported Platforms

For URLs from platforms not in the supported list, the channel will be left empty. The row will still be marked as "processed" (to avoid re-processing), but no channel value will be written.

## Batch Update

The endpoint uses the Google Sheets Batch Update API to efficiently write all channels in a single request:

- All updates are sent in one batch operation
- Maintains data consistency
- Reduces API quota usage

## Notes

- The endpoint requires **write permissions** (Editor role) on the Google Sheet
- Channel extraction handles URL variations and edge cases
- Case sensitivity is preserved from the original URL
- Special characters in channel names are maintained
- The first row is treated as headers
- Empty cells in the Channel column will be filled; existing values are preserved
- Archive URL column is used to identify already-processed rows
- Processing is idempotent - running multiple times won't duplicate work
- Unsupported platforms: URLs from platforms not listed above will be skipped
