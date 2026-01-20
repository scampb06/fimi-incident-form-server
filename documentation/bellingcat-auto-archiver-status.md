# GET /bellingcat/auto-archiver/status/{jobId}

## Overview

Retrieves the current status and details of a running or completed Auto-Archiver background job. This endpoint provides real-time progress information, execution logs, error messages, and results for jobs started via the `/bellingcat/auto-archiver-sheets-asynchronous` endpoint.

## HTTP Method

`GET`

## Parameters

### Path Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `jobId` | string | Yes | The unique job identifier returned when starting the Auto-Archiver job |

### Example Request

```
GET /bellingcat/auto-archiver/status/job-20240115-abc123
```

## Response

### Success Response - Job Running (200 OK)

```json
{
  "jobId": "job-20240115-abc123",
  "jobType": "google-sheets-aci",
  "status": "running",
  "startTime": "2024-01-15T10:30:00Z",
  "endTime": null,
  "duration": "00:05:23.4567890",
  "spreadsheetId": "1ABC123xyz",
  "urls": null,
  "urlCount": 25,
  "outputDirectory": null,
  "containerGroupName": "auto-archiver-job-20240115-abc123",
  "logOutput": "Container starting...\nProcessing URL 1/25...\nProcessing URL 2/25...",
  "errorMessage": null,
  "results": null,
  "note": null
}
```

### Success Response - Job Completed (200 OK)

**For ACI/Docker Jobs (results in Google Sheet):**
```json
{
  "jobId": "job-20240115-abc123",
  "jobType": "google-sheets-aci",
  "status": "completed",
  "startTime": "2024-01-15T10:30:00Z",
  "endTime": "2024-01-15T10:45:30Z",
  "duration": "00:15:30.1234567",
  "spreadsheetId": "1ABC123xyz",
  "urls": null,
  "urlCount": 25,
  "outputDirectory": null,
  "containerGroupName": "auto-archiver-job-20240115-abc123",
  "logOutput": "...complete log output...",
  "errorMessage": null,
  "results": null,
  "note": "Results written to Google Sheet."
}
```

**For Local Jobs (with file results):**
```json
{
  "jobId": "job-20240115-xyz789",
  "jobType": "google-sheets",
  "status": "completed",
  "startTime": "2024-01-15T11:00:00Z",
  "endTime": "2024-01-15T11:12:45Z",
  "duration": "00:12:45.6789012",
  "spreadsheetId": "1ABC123xyz",
  "urls": null,
  "urlCount": 25,
  "outputDirectory": "C:\\archives\\job-20240115-xyz789",
  "containerGroupName": null,
  "logOutput": "...complete log output...",
  "errorMessage": null,
  "results": {
    "totalFiles": 87,
    "screenshots": 25,
    "archives": 20,
    "metadata": 30,
    "files": [
      {
        "name": "screenshot_001.png",
        "path": "screenshots\\screenshot_001.png",
        "size": 245678,
        "created": "2024-01-15T11:05:12"
      },
      {
        "name": "archive_001.warc",
        "path": "archives\\archive_001.warc",
        "size": 1234567,
        "created": "2024-01-15T11:05:15"
      }
      // ... up to 20 files shown
    ],
    "outputPath": "C:\\archives\\job-20240115-xyz789"
  },
  "note": null
}
```

### Success Response - Job Failed (200 OK)

```json
{
  "jobId": "job-20240115-abc123",
  "jobType": "google-sheets-aci",
  "status": "failed",
  "startTime": "2024-01-15T10:30:00Z",
  "endTime": "2024-01-15T10:35:00Z",
  "duration": "00:05:00.0000000",
  "spreadsheetId": "1ABC123xyz",
  "urls": null,
  "urlCount": 25,
  "outputDirectory": null,
  "containerGroupName": "auto-archiver-job-20240115-abc123",
  "logOutput": "...log output with error details...",
  "errorMessage": "Container failed to start: Image pull failed",
  "results": null,
  "note": null
}
```

### Error Response - Job Not Found (404 Not Found)

```json
{
  "message": "Job not found",
  "jobId": "invalid-job-id"
}
```

## Job Status Values

| Status | Description |
|--------|-------------|
| `starting` | Job is initializing (container creating, environment setup) |
| `running` | Job is actively processing URLs |
| `completed` | Job finished successfully |
| `failed` | Job encountered an error and stopped |

## Job Types

| Job Type | Description |
|----------|-------------|
| `google-sheets` | Local or Docker-based Google Sheets archiving |
| `google-sheets-aci` | Azure Container Instance-based Google Sheets archiving |
| `url-list` | List of specific URLs (non-sheets job) |

## Response Fields

| Field | Type | Description |
|-------|------|-------------|
| `jobId` | string | Unique job identifier |
| `jobType` | string | Type of archiving job |
| `status` | string | Current job status |
| `startTime` | datetime | When the job started (UTC) |
| `endTime` | datetime? | When the job ended (null if running) |
| `duration` | timespan | How long the job has been/was running |
| `spreadsheetId` | string? | Google Sheet ID (for sheet jobs) |
| `urls` | array? | List of URLs (for url-list jobs) |
| `urlCount` | number | Total number of URLs to process |
| `outputDirectory` | string? | Local output path (local jobs only) |
| `containerGroupName` | string? | Azure container name (ACI jobs only) |
| `logOutput` | string | Job execution logs |
| `errorMessage` | string? | Error details (if failed) |
| `results` | object? | File results summary (local jobs only) |
| `note` | string? | Additional information |

## Results Object (Local Jobs Only)

When a local job completes, the `results` field contains:

| Field | Type | Description |
|-------|------|-------------|
| `totalFiles` | number | Total archived files |
| `screenshots` | number | Number of screenshot files (.png, .jpg) |
| `archives` | number | Number of archive files (.warc, .zip) |
| `metadata` | number | Number of metadata files (.json, .yaml) |
| `files` | array | Array of up to 20 file details |
| `outputPath` | string | Full path to output directory |

## Polling Strategy

For monitoring long-running jobs:

1. **Initial Request**: Immediately after starting job
2. **Polling Interval**: Every 30-60 seconds while status is `starting` or `running`
3. **Completion**: Stop polling when status is `completed` or `failed`

### Example Polling Logic

```javascript
async function pollJobStatus(jobId, maxAttempts = 100) {
  for (let i = 0; i < maxAttempts; i++) {
    const response = await fetch(`/bellingcat/auto-archiver/status/${jobId}`);
    const status = await response.json();
    
    console.log(`[${i + 1}] Status: ${status.status} - Duration: ${status.duration}`);
    
    if (status.status === 'completed') {
      console.log('✓ Job completed successfully');
      if (status.results) {
        console.log(`Files created: ${status.results.totalFiles}`);
      }
      return status;
    }
    
    if (status.status === 'failed') {
      console.error('✗ Job failed:', status.errorMessage);
      return status;
    }
    
    // Wait before next poll (30 seconds for starting, 60 for running)
    const waitTime = status.status === 'starting' ? 30000 : 60000;
    await new Promise(resolve => setTimeout(resolve, waitTime));
  }
  
  throw new Error('Max polling attempts reached');
}

// Usage
const status = await pollJobStatus('job-20240115-abc123');
```

## Example Usage

```bash
# Check job status
curl "https://your-app.azurewebsites.net/bellingcat/auto-archiver/status/job-20240115-abc123"
```

```javascript
// JavaScript fetch example
async function checkJobStatus(jobId) {
  const response = await fetch(`/bellingcat/auto-archiver/status/${jobId}`);
  const status = await response.json();
  
  console.log(`Job ${jobId}:`);
  console.log(`  Status: ${status.status}`);
  console.log(`  Duration: ${status.duration}`);
  console.log(`  URLs: ${status.urlCount}`);
  
  if (status.status === 'completed') {
    if (status.note) {
      console.log(`  Note: ${status.note}`);
    }
    if (status.results) {
      console.log(`  Files: ${status.results.totalFiles}`);
    }
  }
  
  if (status.errorMessage) {
    console.error(`  Error: ${status.errorMessage}`);
  }
  
  return status;
}

// Usage
await checkJobStatus('job-20240115-abc123');
```

## Log Output

The `logOutput` field contains console output from the Auto-Archiver process, including:

- Container startup messages (ACI mode)
- URL processing progress
- Archive results for each URL
- Error messages and warnings
- Completion summary

**ACI Example:**
```
Container instance created successfully
Pulling image: myregistry.azurecr.io/auto-archiver-custom:latest
Image pulled successfully
Starting container...
Running auto-archiver with Google Sheets configuration
Processing URL 1/25: https://example.com
✓ Archived to https://web.archive.org/...
Processing URL 2/25: https://test.com
...
```

## Notes

- Jobs are stored in memory - restarting the application will clear job history
- For production, consider implementing persistent job storage (database)
- ACI jobs automatically clean up containers after completion
- Container logs for ACI jobs are also available in Azure Log Analytics
- The endpoint returns 404 if the job ID doesn't exist
- Status checks are lightweight and can be called frequently
- Duration is calculated from start time to current time (running jobs) or end time (completed jobs)
- Results are only available for local/Docker jobs, not ACI jobs (which write directly to sheets)
