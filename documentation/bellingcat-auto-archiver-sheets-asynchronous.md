# POST /bellingcat/auto-archiver-sheets-asynchronous

## Overview

Starts a background job to archive URLs in a Google Sheet using the Bellingcat Auto-Archiver tool. This endpoint supports multiple deployment modes (Azure Container Instances, Docker, or local Python) and provides comprehensive archiving capabilities including screenshots, WACZ files, metadata extraction, and storage to AWS S3 or Google Drive. The job runs asynchronously and writes results directly to the Google Sheet.

## HTTP Method

`POST`

## Parameters

### Query Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `url` | string | Yes | Full Google Sheets URL |

### Example Request

```
POST /bellingcat/auto-archiver-sheets-asynchronous?url=https://docs.google.com/spreadsheets/d/1ABC123/edit#gid=0
```

## Response

### Success Response (200 OK)

```json
{
  "message": "Auto-Archiver Google Sheets job started successfully",
  "jobId": "job-20240115-abc123",
  "jobType": "google-sheets",
  "status": "starting",
  "installMode": "aci",
  "spreadsheetId": "1ABC123xyz",
  "sheetName": "Sheet1",
  "estimatedUrlCount": 25,
  "videoUrlCount": 5,
  "regularUrlCount": 20,
  "estimatedTime": "13.0-28.0 minutes",
  "checkStatusUrl": "/bellingcat/auto-archiver/status/job-20240115-abc123",
  "note": "Azure Container Instance is being created. This may take 2-3 minutes for image pull. Use Check Status to monitor progress."
}
```

**For Docker/Local Mode:**
```json
{
  "message": "Auto-Archiver Google Sheets job started successfully",
  "jobId": "job-20240115-xyz789",
  "jobType": "google-sheets",
  "status": "starting",
  "installMode": "docker",
  "spreadsheetId": "1ABC123xyz",
  "sheetName": "Sheet1",
  "estimatedUrlCount": 25,
  "videoUrlCount": 5,
  "regularUrlCount": 20,
  "estimatedTime": "10.0-25.0 minutes",
  "checkStatusUrl": "/bellingcat/auto-archiver/status/job-20240115-xyz789",
  "note": "Use the status endpoint to monitor progress. Auto-Archiver will write results directly to the Google Sheet."
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

**413 Payload Too Large** - Estimated time exceeds container limit (ACI mode only)
```json
{
  "type": "about:blank",
  "title": "One or more errors occurred.",
  "status": 413,
  "detail": "Estimated processing time (45.0 minutes) exceeds the container maximum wait time (180 minutes). Please either increase the 'AutoArchiver__ContainerMaxWaitMinutes' app setting in Azure, or break your Google Sheet into smaller chunks. Current sheet has 500 URLs (50 videos, 450 regular URLs)."
}
```

**500 Internal Server Error** - Environment setup failed
```json
{
  "type": "about:blank",
  "title": "One or more errors occurred.",
  "status": 500,
  "detail": {...}
}
```

## Deployment Modes

The endpoint supports three installation modes (configured via `AutoArchiver:Install`):

### 1. Azure Container Instances (ACI) - Recommended for Production

**Configuration:** `"AutoArchiver:Install": "aci"`

**Features:**
- Runs in isolated Azure containers
- Automatic cleanup after completion
- Scalable and reliable
- Integrated with Azure Log Analytics

**Requirements:**
- Azure Container Registry with custom Auto-Archiver image
- Azure Container Instances resource group
- Managed identity with Contributor permissions

**Advantages:**
- No local dependencies
- Handles large workloads
- Container logs available in Azure Portal

### 2. Docker - Good for Development/Testing

**Configuration:** `"AutoArchiver:Install": "docker"`

**Features:**
- Runs in local Docker containers
- Uses official Bellingcat image
- Faster startup than ACI

**Requirements:**
- Docker installed and running
- `bellingcat/auto-archiver:latest` image pulled

**Command to pull image:**
```bash
docker pull bellingcat/auto-archiver:latest
```

### 3. Local Python - For Development Only

**Configuration:** `"AutoArchiver:Install": "local"`

**Features:**
- Runs directly with Python
- Requires local Auto-Archiver installation

**Requirements:**
- Python 3.8+
- `auto-archiver` package installed
- `orchestration_sheets.yaml` configuration file

**Installation:**
```bash
pip install auto-archiver
```

## Sheet Requirements

### Required Columns

The Google Sheet must have these columns (Auto-Archiver will create missing ones):

- **URL**: URLs to archive

### Auto-Created Columns

Auto-Archiver will create these columns if they don't exist:

- **Archive Status**: Status of archiving
- **Destination Folder**: Where archives are stored
- **Archive URL**: Link to archived content
- **Archive Date**: When archiving occurred
- **Thumbnail**: Screenshot thumbnail
- **Upload Timestamp**: Media upload time (if available)
- **Upload Title**: Original media title
- **Text Content**: Extracted text
- **Screenshot**: Screenshot URL
- **Hash**: Content hash
- **Perceptual Hashes**: PDQ hashes for media
- **WACZ**: Web Archive Collection Zipped file
- **ReplayWebpage**: Replay page URL

## Time Estimates

The endpoint calculates estimated processing time based on URL types:

- **Video URLs**: 2-5 minutes each
- **Regular URLs**: 15-30 seconds each
- **ACI Mode**: +3 minutes for container image pull

### Example Calculations

| URL Count | Video URLs | Regular URLs | Estimated Time |
|-----------|------------|--------------|----------------|
| 10 | 2 | 8 | 4.2-10.2 min (ACI), 1.2-7.2 min (Docker) |
| 50 | 10 | 40 | 13.0-28.0 min (ACI), 10.0-25.0 min (Docker) |
| 100 | 20 | 80 | 23.0-53.0 min (ACI), 20.0-50.0 min (Docker) |

## Configuration Requirements

### Required Application Settings

**Google Sheets:**
- `GoogleSheets:ServiceAccount:client_email`
- `GoogleSheets:ServiceAccount:private_key`
- `GoogleSheets:ServiceAccount:token_uri`

**Storage (AWS S3 or Google Drive):**

For S3:
- `AutoArchiver:S3Storage:Bucket`
- `AutoArchiver:S3Storage:Region`
- `AutoArchiver:S3Storage:Key`
- `AutoArchiver:S3Storage:Secret`
- `AutoArchiver:S3Storage:EndpointUrl` (optional)
- `AutoArchiver:S3Storage:CdnUrl` (optional)

For Google Drive:
- `AutoArchiver:GDriveStorage:RootFolderId`

**Azure (for ACI mode):**
- `AzureContainerInstance:SubscriptionId`
- `AzureContainerInstance:ResourceGroupName`
- `AzureContainerInstance:Location`
- `AzureContainerInstance:ContainerImage`
- `AzureContainerInstance:RegistryServer`
- `AzureContainerInstance:RegistryUsername`
- `AzureContainerInstance:RegistryPassword`
- `AzureStorage:AccountName`
- `AzureStorage:AccountKey`
- `AzureStorage:FileShareName`
- `AzureLogAnalytics:WorkspaceId`
- `AzureLogAnalytics:WorkspaceKey`

### Optional Settings

- `AutoArchiver:Install`: Deployment mode (`aci`, `docker`, `local`)
- `AutoArchiver:ContainerMaxWaitMinutes`: Max container runtime (default: 180)
- `AutoArchiver:WaybackEnricher:Key`: Internet Archive API key
- `AutoArchiver:WaybackEnricher:Secret`: Internet Archive API secret

**Required Permission:** Service account must have **Editor** access to the Google Sheet.

## Job Monitoring

After starting the job, use the status endpoint to monitor progress:

```
GET /bellingcat/auto-archiver/status/{jobId}
```

See [bellingcat-auto-archiver-status.md](bellingcat-auto-archiver-status.md) for details.

## Example Usage

```bash
curl -X POST "https://your-app.azurewebsites.net/bellingcat/auto-archiver-sheets-asynchronous?url=https%3A%2F%2Fdocs.google.com%2Fspreadsheets%2Fd%2F1ABC123%2Fedit%23gid%3D0"
```

```javascript
// JavaScript fetch example
async function startAutoArchiver(sheetUrl) {
  const encodedUrl = encodeURIComponent(sheetUrl);
  
  const response = await fetch(
    `/bellingcat/auto-archiver-sheets-asynchronous?url=${encodedUrl}`,
    { method: 'POST' }
  );
  
  const result = await response.json();
  console.log(`Job started: ${result.jobId}`);
  console.log(`Estimated time: ${result.estimatedTime}`);
  console.log(`URLs to process: ${result.estimatedUrlCount}`);
  
  // Poll status endpoint
  const checkStatus = async () => {
    const statusResponse = await fetch(result.checkStatusUrl);
    const status = await statusResponse.json();
    console.log(`Status: ${status.status}`);
    
    if (status.status === 'completed' || status.status === 'failed') {
      clearInterval(interval);
      console.log('Job finished:', status);
    }
  };
  
  const interval = setInterval(checkStatus, 30000); // Check every 30 seconds
  
  return result;
}

// Usage
await startAutoArchiver('https://docs.google.com/spreadsheets/d/1ABC123/edit#gid=0');
```

## Auto-Archiver Features

The Bellingcat Auto-Archiver provides comprehensive archiving:

- **Screenshots**: Full page screenshots
- **WACZ Archives**: Web Archive Collection Zipped format
- **Media Download**: Videos, images, and other media
- **Metadata Extraction**: Title, timestamp, author, etc.
- **Hash Generation**: Content and perceptual hashes
- **Multiple Storage**: S3, Google Drive support
- **Platform Support**: Twitter, YouTube, Facebook, TikTok, Instagram, Telegram, and more

## Notes

- Jobs run asynchronously in the background
- Results are written directly to the Google Sheet
- For ACI mode, containers are automatically created and cleaned up
- Container logs are available in Azure Log Analytics
- The endpoint validates sheet structure and creates missing columns
- Large sheets (500+ URLs) may take several hours to process
- Processing time varies based on URL complexity and platform
- Failed URLs are marked with error details in the Archive Status column
- The job continues processing even if individual URLs fail
