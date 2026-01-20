# GET /cors-proxy/pdf

## Overview

Acts as a CORS proxy to bypass Cross-Origin Resource Sharing restrictions when accessing PDF files from servers that don't allow direct browser access. The endpoint fetches PDFs from remote servers and streams them to the client with appropriate CORS headers, while handling rate limiting, timeouts, and network errors with automatic retries.

## HTTP Method

`GET`

## Parameters

### Query Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `url` | string | Yes | The URL of the PDF file to fetch |

### Example Request

```
GET /cors-proxy/pdf?url=https://example.com/document.pdf
```

## Response

### Success Response (200 OK)

Returns the PDF file with CORS headers enabled.

**Headers:**
- `Content-Type`: `application/pdf`
- `Access-Control-Allow-Origin`: `*`
- `Access-Control-Allow-Headers`: `*`
- `X-Proxy-Delay`: `{seconds}s` (only present if retries occurred)
- `X-Proxy-Retries`: `{count}` (only present if retries occurred)
- `Content-Length`: PDF file size in bytes (if available)

**Body:** Binary PDF content

### Error Responses

**400 Bad Request** - Missing URL parameter
```json
{
  "message": "URL parameter is required."
}
```

**404 Not Found** - PDF not found at URL
```json
{
  "message": "PDF not found at the specified URL."
}
```

**403 Forbidden** - Access denied
```json
{
  "type": "about:blank",
  "title": "One or more errors occurred.",
  "status": 403,
  "detail": "Access to PDF is forbidden."
}
```

**408 Request Timeout** - Request timed out
```json
{
  "type": "about:blank",
  "title": "One or more errors occurred.",
  "status": 408,
  "detail": "Request timeout while fetching PDF. The server may be slow or unreachable."
}
```

**429 Too Many Requests** - Rate limited
```json
{
  "type": "about:blank",
  "title": "One or more errors occurred.",
  "status": 429,
  "detail": "Server is rate limiting requests. Please try again later."
}
```

**502 Bad Gateway** - Network error or max retries exceeded
```json
{
  "type": "about:blank",
  "title": "One or more errors occurred.",
  "status": 502,
  "detail": "Network error while fetching PDF: {error message}"
}
```

## Features

### Retry Logic

The endpoint automatically retries failed requests with the following behavior:

- **Max Retries**: 3 attempts
- **Timeout per Request**: 15 seconds
- **Max Total Wait Time**: 25 seconds
- **Rate Limit Handling**: Respects `Retry-After` headers, uses exponential backoff (2s, 4s, 8s max)
- **Timeout Handling**: Progressive delays (1s, 2s, 3s) between retries

### Browser-Like Headers

Requests include browser-like headers to appear more legitimate:
- `User-Agent`: Mozilla/5.0 (Windows NT 10.0; Win64; x64)...
- `Accept`: application/pdf,application/octet-stream,*/*;q=0.8
- `Accept-Language`: en-US,en;q=0.9
- `Accept-Encoding`: gzip, deflate, br
- `Cache-Control`: no-cache
- `Pragma`: no-cache

## Example Usage

```bash
curl "https://your-app.azurewebsites.net/cors-proxy/pdf?url=https://example.com/report.pdf" \
  --output downloaded.pdf
```

```javascript
// JavaScript fetch example
const pdfUrl = encodeURIComponent('https://example.com/report.pdf');
const response = await fetch(`/cors-proxy/pdf?url=${pdfUrl}`);

if (response.ok) {
  const blob = await response.blob();
  const url = URL.createObjectURL(blob);
  window.open(url);
} else {
  console.error('Failed to fetch PDF:', response.status);
}
```

```html
<!-- Direct iframe embedding -->
<iframe 
  src="/cors-proxy/pdf?url=https://example.com/report.pdf" 
  width="100%" 
  height="600px">
</iframe>
```

## Notes

- The endpoint sets `Access-Control-Allow-Origin: *` - in production, consider restricting to specific origins
- PDFs are streamed directly to the client (not buffered in memory)
- Rate limiting responses include delay information in custom headers
- All network errors are logged to the console for debugging
- The proxy does not cache PDFs - each request fetches from the source
- URL parameter should be URL-encoded if it contains special characters
