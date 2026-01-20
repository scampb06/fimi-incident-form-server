# POST /generate-text

## Overview

Uses OpenAI's ChatGPT (GPT-3.5-turbo) to generate text summaries based on a provided prompt. The endpoint is specifically configured with a system prompt for FIMI (Foreign Information Manipulation and Interference) incident report analysis, providing clear, structured, and professional summaries.

## HTTP Method

`POST`

## Parameters

### Request Body (JSON)

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `prompt` | string | Yes | The text prompt to send to ChatGPT for processing |

### Example Request Body

```json
{
  "prompt": "Summarize the following incident: A coordinated disinformation campaign was detected across social media platforms..."
}
```

## Response

### Success Response (200 OK)

Returns the complete OpenAI API response containing the generated text and metadata.

```json
{
  "id": "chatcmpl-...",
  "object": "chat.completion",
  "created": 1234567890,
  "model": "gpt-3.5-turbo-0125",
  "choices": [
    {
      "index": 0,
      "message": {
        "role": "assistant",
        "content": "The generated summary text..."
      },
      "finish_reason": "stop"
    }
  ],
  "usage": {
    "prompt_tokens": 150,
    "completion_tokens": 200,
    "total_tokens": 350
  }
}
```

### Error Responses

**400 Bad Request** - Missing or empty prompt
```json
{
  "message": "Prompt is required."
}
```

**OpenAI API Error** - Error from OpenAI service
```json
{
  "type": "about:blank",
  "title": "Error from OpenAI API",
  "status": 401,
  "detail": "Incorrect API key provided..."
}
```

## Configuration Requirements

The following application settings must be configured:

- `OpenAIKey`: Your OpenAI API key

## Model Configuration

- **Model**: `gpt-3.5-turbo`
- **Max Tokens**: 1000
- **Temperature**: 0.3 (lower temperature for more focused, consistent output)
- **System Prompt**: "You are a FIMI (Foreign Information Manipulation and Interference) analyst expert at summarizing incident reports. Provide clear, structured, and professional summaries."

## Example Usage

```bash
curl -X POST https://your-app.azurewebsites.net/generate-text \
  -H "Content-Type: application/json" \
  -d '{"prompt": "Analyze this disinformation campaign..."}'
```

```javascript
// JavaScript fetch example
const response = await fetch('/generate-text', {
  method: 'POST',
  headers: {
    'Content-Type': 'application/json'
  },
  body: JSON.stringify({
    prompt: 'Summarize the following FIMI incident...'
  })
});

const result = await response.json();
console.log(result.choices[0].message.content);
```

## Notes

- The endpoint uses OpenAI's streaming API endpoint (`https://api.openai.com/v1/chat/completions`)
- Responses are limited to 1000 tokens
- The temperature setting (0.3) is optimized for consistent, professional summaries
- API costs apply based on token usage (see OpenAI pricing)
