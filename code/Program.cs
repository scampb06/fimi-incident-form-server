/*
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "Hello World!");

app.Run();
*/
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text;
using System.Web;
using System.Security.Cryptography;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ContainerInstance;
using Azure.ResourceManager.ContainerInstance.Models;
using Azure.ResourceManager.Resources;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddHttpClient(); // For making HTTP calls to OpenAI
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(
        policy =>
        {
            policy.AllowAnyOrigin() // Adjust for production security
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        });
});

var app = builder.Build();

app.UseCors(); // Enable CORS

// In-memory job tracking for Auto-Archiver (in production, use a database)
var activeJobs = new Dictionary<string, AutoArchiverJob>();
var autoArchiverJobsLock = new object();

app.MapPost("/generate-text", async (HttpRequest request, IHttpClientFactory httpClientFactory, IConfiguration configuration) =>
{
    // Read the request body to get the prompt
    using var reader = new StreamReader(request.Body);
    var requestBody = await reader.ReadToEndAsync();
    var jsonDoc = JsonDocument.Parse(requestBody);
    var prompt = jsonDoc.RootElement.GetProperty("prompt").GetString();

    if (string.IsNullOrEmpty(prompt))
    {
        return Results.BadRequest(new { message = "Prompt is required." });
    }

    var httpClient = httpClientFactory.CreateClient();
    var openAiKey = configuration["OpenAIKey"];
    httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {openAiKey}");
    
    var openaiRequest = new
    {
        model = "gpt-3.5-turbo", // Or another suitable model
        messages = new[] 
        { 
            new { role = "system", content = "You are a FIMI (Foreign Information Manipulation and Interference) analyst expert at summarizing incident reports. Provide clear, structured, and professional summaries." },
            new { role = "user", content = prompt }
        },
        max_tokens = 1000,
        temperature = 0.3
    };

    var content = new StringContent(JsonSerializer.Serialize(openaiRequest), System.Text.Encoding.UTF8, "application/json");

    var response = await httpClient.PostAsync("https://api.openai.com/v1/chat/completions", content);

    if (response.IsSuccessStatusCode)
    {
        var openaiResponse = await response.Content.ReadAsStringAsync();
        return Results.Ok(JsonDocument.Parse(openaiResponse));
    }
    else
    {
        var errorContent = await response.Content.ReadAsStringAsync();
        return Results.Problem(
            detail: errorContent,
            statusCode: (int)response.StatusCode,
            title: "Error from OpenAI API"
        );
    }
});

/* app.MapGet("/cors-proxy/pdf", async (HttpContext context, IHttpClientFactory httpClientFactory) => {
    var url = context.Request.Query["url"];
    if (string.IsNullOrEmpty(url))
        return Results.BadRequest(new { message = "URL parameter is required." });

    var client = httpClientFactory.CreateClient();
    var pdfResponse = await client.GetAsync(url);

    if (!pdfResponse.IsSuccessStatusCode)
        return Results.StatusCode((int)pdfResponse.StatusCode);

    context.Response.Headers["Access-Control-Allow-Origin"] = "*"; // For testing; use specific origin(s) in production
    context.Response.ContentType = "application/pdf";
    await pdfResponse.Content.CopyToAsync(context.Response.Body);

    return Results.Ok(); // This line helps Minimal API but won't send extra content
}); */

app.MapGet("/cors-proxy/pdf", async (HttpContext context, IHttpClientFactory httpClientFactory) =>
{
    var url = context.Request.Query["url"];
    if (string.IsNullOrEmpty(url))
        return Results.BadRequest(new { message = "URL parameter is required." });

    try
    {
        var client = httpClientFactory.CreateClient();
        
        // Add browser-like headers to appear more legitimate
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept", "application/pdf,application/octet-stream,*/*;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
        client.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
        client.DefaultRequestHeaders.Add("Pragma", "no-cache");
        
        // Set timeout for individual requests
        client.Timeout = TimeSpan.FromSeconds(15);

        HttpResponseMessage? pdfResponse = null;
        int maxRetries = 3;
        int currentAttempt = 0;
        int totalWaitTime = 0;
        const int maxTotalWaitTime = 25; // Maximum 25 seconds total wait time

        while (currentAttempt < maxRetries)
        {
            currentAttempt++;
            
            try
            {
                Console.WriteLine($"PDF Proxy: Attempt {currentAttempt} for URL: {url}");
                pdfResponse = await client.GetAsync(url);

                // Handle rate limiting (429)
                if (pdfResponse.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    Console.WriteLine($"PDF Proxy: Received 429 Too Many Requests on attempt {currentAttempt}");
                    
                    // Check if we've exceeded our max wait time
                    if (totalWaitTime >= maxTotalWaitTime)
                    {
                        Console.WriteLine($"PDF Proxy: Exceeded maximum wait time ({maxTotalWaitTime}s), giving up");
                        return Results.Problem("Server is rate limiting requests. Please try again later.", statusCode: 429);
                    }
                    
                    // Calculate wait time
                    int waitSeconds = 0;
                    
                    // Check for Retry-After header
                    if (pdfResponse.Headers.RetryAfter != null)
                    {
                        if (pdfResponse.Headers.RetryAfter.Delta.HasValue)
                        {
                            waitSeconds = (int)pdfResponse.Headers.RetryAfter.Delta.Value.TotalSeconds;
                        }
                        else if (pdfResponse.Headers.RetryAfter.Date.HasValue)
                        {
                            waitSeconds = (int)(pdfResponse.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow).TotalSeconds;
                        }
                    }
                    
                    // If no Retry-After header, use exponential backoff
                    if (waitSeconds <= 0)
                    {
                        waitSeconds = Math.Min(2 * currentAttempt, 8); // 2, 4, 8 seconds max
                    }
                    
                    // Ensure we don't exceed max total wait time
                    waitSeconds = Math.Min(waitSeconds, maxTotalWaitTime - totalWaitTime);
                    
                    if (waitSeconds > 0 && currentAttempt < maxRetries)
                    {
                        Console.WriteLine($"PDF Proxy: Waiting {waitSeconds} seconds before retry (total wait: {totalWaitTime + waitSeconds}s)");
                        await Task.Delay(waitSeconds * 1000);
                        totalWaitTime += waitSeconds;
                        pdfResponse.Dispose(); // Clean up the failed response
                        continue; // Retry
                    }
                    else
                    {
                        // No more retries or would exceed max wait time
                        return Results.Problem("Server is rate limiting requests. Please try again later.", statusCode: 429);
                    }
                }
                
                // Handle other HTTP errors
                if (!pdfResponse.IsSuccessStatusCode)
                {
                    Console.WriteLine($"PDF Proxy: HTTP {(int)pdfResponse.StatusCode} {pdfResponse.StatusCode} on attempt {currentAttempt}");
                    
                    // For non-429 errors, fail immediately
                    if (pdfResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        return Results.NotFound(new { message = "PDF not found at the specified URL." });
                    }
                    else if (pdfResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        return Results.Problem("Access to PDF is forbidden.", statusCode: 403);
                    }
                    else
                    {
                        return Results.StatusCode((int)pdfResponse.StatusCode);
                    }
                }

                // Success! Break out of retry loop
                Console.WriteLine($"PDF Proxy: Successfully retrieved PDF on attempt {currentAttempt}");
                break;
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || ex.Message.Contains("timeout"))
            {
                Console.WriteLine($"PDF Proxy: Request timeout on attempt {currentAttempt}");
                if (currentAttempt >= maxRetries)
                {
                    return Results.Problem("Request timeout while fetching PDF. The server may be slow or unreachable.", statusCode: 408);
                }
                // Wait before retry
                await Task.Delay(1000 * currentAttempt); // 1s, 2s, 3s
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"PDF Proxy: Network error on attempt {currentAttempt}: {ex.Message}");
                if (currentAttempt >= maxRetries)
                {
                    return Results.Problem($"Network error while fetching PDF: {ex.Message}", statusCode: 502);
                }
                // Wait before retry
                await Task.Delay(1000 * currentAttempt); // 1s, 2s, 3s
            }
        }

        // If we still don't have a successful response, something went wrong
        if (pdfResponse == null || !pdfResponse.IsSuccessStatusCode)
        {
            return Results.Problem("Failed to retrieve PDF after multiple attempts.", statusCode: 502);
        }

        // Set all headers BEFORE starting the response
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/pdf";
        context.Response.Headers["Access-Control-Allow-Origin"] = "*"; // For testing; use specific origin(s) in production
        context.Response.Headers["Access-Control-Allow-Headers"] = "*";
        
        // Add info about any delays that occurred
        if (totalWaitTime > 0)
        {
            context.Response.Headers["X-Proxy-Delay"] = $"{totalWaitTime}s";
            context.Response.Headers["X-Proxy-Retries"] = currentAttempt.ToString();
        }

        // Optional: Set content length if available (helps with chunked encoding)
        if (pdfResponse.Content.Headers.ContentLength.HasValue)
        {
            context.Response.Headers["Content-Length"] = pdfResponse.Content.Headers.ContentLength.Value.ToString();
        }

        // Stream the PDF content (this starts the response)
        await pdfResponse.Content.CopyToAsync(context.Response.Body);

        // Return an empty result since we've already written to the response
        return Results.Empty;
    }
    catch (Exception ex)
    {
        // Only set status if response hasn't started
        if (!context.Response.HasStarted)
        {
            return Results.Problem($"Error fetching PDF: {ex.Message}", statusCode: 500);
        }
        // If response has started, we can't return a result
        // Return empty result as fallback
        return Results.Empty;
    }
});

app.MapGet("/google-sheets/data", async (IHttpClientFactory httpClientFactory, IConfiguration configuration) =>
{
    try
    {
        var spreadsheetId = configuration["GoogleSheets:SpreadsheetId"];
        var sheetName = configuration["GoogleSheets:SheetName"];
        
        if (string.IsNullOrEmpty(spreadsheetId) || string.IsNullOrEmpty(sheetName))
        {
            return Results.BadRequest(new { message = "SpreadsheetId and SheetName must be configured" });
        }
        
        // Get service account configuration
        var serviceAccountSection = configuration.GetSection("GoogleSheets:ServiceAccount");
        var clientEmail = serviceAccountSection["client_email"];
        var privateKey = serviceAccountSection["private_key"];
        var tokenUri = serviceAccountSection["token_uri"];

        if (string.IsNullOrEmpty(clientEmail) || string.IsNullOrEmpty(privateKey) || string.IsNullOrEmpty(tokenUri))
        {
            return Results.BadRequest(new { message = "Service account configuration is missing" });
        }

        var httpClient = httpClientFactory.CreateClient();
        
        // Get access token using service account
        var accessToken = await GetServiceAccountAccessToken(httpClient, clientEmail, privateKey, tokenUri);
        
        if (string.IsNullOrEmpty(accessToken))
        {
            return Results.Problem("Failed to authenticate with Google Sheets API", statusCode: 401);
        }

        // Call Google Sheets API
        var apiUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}/values/{Uri.EscapeDataString(sheetName)}";
        
        httpClient.DefaultRequestHeaders.Clear();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
        
        var response = await httpClient.GetAsync(apiUrl);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            return Results.Problem($"Google Sheets API error: {errorContent}", statusCode: (int)response.StatusCode);
        }

        var responseContent = await response.Content.ReadAsStringAsync();
        var sheetsData = JsonDocument.Parse(responseContent);
        var records = ConvertGoogleApiToRecords(sheetsData);
        
        return Results.Ok(new { data = records, count = records.Count, method = "Service Account API" });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error fetching Google Sheets data: {ex.Message}", statusCode: 500);
    }
});

app.MapGet("/google-sheets/data-for-url", async (HttpContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration) =>
{
    try
    {
        var url = context.Request.Query["url"];
        
        if (string.IsNullOrEmpty(url))
        {
            return Results.BadRequest(new { message = "URL parameter is required" });
        }

        // Extract spreadsheet ID and GID from Google Sheets URL
        var (spreadsheetId, gid) = ExtractSpreadsheetInfo(url!);
        
        if (string.IsNullOrEmpty(spreadsheetId))
        {
            return Results.BadRequest(new { 
                message = "Invalid Google Sheets URL. Expected format: https://docs.google.com/spreadsheets/d/{SPREADSHEET_ID}/edit#gid={SHEET_ID}",
                providedUrl = url.ToString()
            });
        }
        
        // Get service account configuration
        var serviceAccountSection = configuration.GetSection("GoogleSheets:ServiceAccount");
        var clientEmail = serviceAccountSection["client_email"];
        var privateKey = serviceAccountSection["private_key"];
        var tokenUri = serviceAccountSection["token_uri"];

        if (string.IsNullOrEmpty(clientEmail) || string.IsNullOrEmpty(privateKey) || string.IsNullOrEmpty(tokenUri))
        {
            return Results.BadRequest(new { message = "Service account configuration is missing" });
        }

        var httpClient = httpClientFactory.CreateClient();
        
        // Get access token using service account
        var accessToken = await GetServiceAccountAccessToken(httpClient, clientEmail, privateKey, tokenUri);
        
        if (string.IsNullOrEmpty(accessToken))
        {
            return Results.Problem("Failed to authenticate with Google Sheets API", statusCode: 401);
        }

        httpClient.DefaultRequestHeaders.Clear();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

        // First, get the spreadsheet metadata to find the correct sheet name
        var metadataUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}";
        var metadataResponse = await httpClient.GetAsync(metadataUrl);
        
        if (!metadataResponse.IsSuccessStatusCode)
        {
            var metadataError = await metadataResponse.Content.ReadAsStringAsync();
            return Results.Problem($"Error getting spreadsheet metadata: {metadataError}", statusCode: (int)metadataResponse.StatusCode);
        }

        var metadataContent = await metadataResponse.Content.ReadAsStringAsync();
        var metadataDoc = JsonDocument.Parse(metadataContent);
        
        // Find the sheet name by GID or use the first sheet
        string sheetName = "Sheet1"; // Default fallback
        var sheets = metadataDoc.RootElement.GetProperty("sheets").EnumerateArray();
        
        foreach (var sheet in sheets)
        {
            var properties = sheet.GetProperty("properties");
            var sheetId = properties.GetProperty("sheetId").GetInt32();
            var title = properties.GetProperty("title").GetString() ?? "Sheet1";
            
            if (gid.HasValue && sheetId == gid.Value)
            {
                sheetName = title;
                break;
            }
            else if (!gid.HasValue && sheet.Equals(sheets.First()))
            {
                // Use first sheet if no GID specified
                sheetName = title;
                break;
            }
        }

        // Call Google Sheets API to get the data
        var apiUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}/values/{Uri.EscapeDataString(sheetName)}";
        
        var response = await httpClient.GetAsync(apiUrl);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            return Results.Problem($"Google Sheets API error: {errorContent}", statusCode: (int)response.StatusCode);
        }

        var responseContent = await response.Content.ReadAsStringAsync();
        var sheetsData = JsonDocument.Parse(responseContent);
        var records = ConvertGoogleApiToRecords(sheetsData);
        
        return Results.Ok(new { 
            data = records, 
            count = records.Count, 
            method = "Service Account API", 
            spreadsheetId = spreadsheetId,
            sheetName = sheetName,
            gid = gid,
            sourceUrl = url.ToString()
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error fetching Google Sheets data: {ex.Message}", statusCode: 500);
    }
});

// Extract spreadsheet ID and GID from Google Sheets URL
(string? spreadsheetId, int? gid) ExtractSpreadsheetInfo(string url)
{
    try
    {
        var uri = new Uri(url);
        
        // Validate that this is a Google Sheets URL
        if (!uri.Host.Contains("docs.google.com") || !uri.AbsolutePath.Contains("/spreadsheets/d/"))
        {
            return (null, null);
        }
        
        // Extract spreadsheet ID from URL path
        // Format: https://docs.google.com/spreadsheets/d/{SPREADSHEET_ID}/edit...
        var pathSegments = uri.AbsolutePath.Split('/');
        var spreadsheetIdIndex = Array.IndexOf(pathSegments, "d") + 1;
        
        if (spreadsheetIdIndex <= 0 || spreadsheetIdIndex >= pathSegments.Length)
        {
            return (null, null);
        }
        
        var spreadsheetId = pathSegments[spreadsheetIdIndex];
        
        // Extract GID from fragment (#gid=123456) or query parameter (?gid=123456)
        int? gid = null;
        
        // First try to get GID from query parameters (?gid=123456)
        if (!string.IsNullOrEmpty(uri.Query))
        {
            var queryParams = HttpUtility.ParseQueryString(uri.Query);
            var gidParam = queryParams["gid"];
            if (!string.IsNullOrEmpty(gidParam) && int.TryParse(gidParam, out var queryGid))
            {
                gid = queryGid;
            }
        }
        
        // If not found in query, try fragment (#gid=123456)
        if (!gid.HasValue && !string.IsNullOrEmpty(uri.Fragment))
        {
            var fragment = uri.Fragment.TrimStart('#');
            var gidMatch = System.Text.RegularExpressions.Regex.Match(fragment, @"gid=(\d+)");
            if (gidMatch.Success && int.TryParse(gidMatch.Groups[1].Value, out var parsedGid))
            {
                gid = parsedGid;
            }
        }
        
        return (spreadsheetId, gid);
    }
    catch
    {
        return (null, null);
    }
}

// Get Google service account access token using JWT
async Task<string?> GetServiceAccountAccessToken(HttpClient httpClient, string clientEmail, string privateKey, string tokenUri)
{
    try
    {
        // Create JWT assertion
        var now = DateTimeOffset.UtcNow;
        var exp = now.AddHours(1);
        
        var header = new
        {
            alg = "RS256",
            typ = "JWT"
        };
        
        var payload = new
        {
            iss = clientEmail,
            scope = "https://www.googleapis.com/auth/spreadsheets.readonly",
            aud = tokenUri,
            exp = exp.ToUnixTimeSeconds(),
            iat = now.ToUnixTimeSeconds()
        };
        
        var headerJson = JsonSerializer.Serialize(header);
        var payloadJson = JsonSerializer.Serialize(payload);
        
        var headerEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(headerJson))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payloadEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        
        var message = $"{headerEncoded}.{payloadEncoded}";
        
        // Sign with RSA private key
        var signature = SignWithRSA(message, privateKey);
        var signatureEncoded = Convert.ToBase64String(signature)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        
        var jwt = $"{message}.{signatureEncoded}";
        
        // Exchange JWT for access token
        var tokenRequest = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
            new KeyValuePair<string, string>("assertion", jwt)
        });
        
        var tokenResponse = await httpClient.PostAsync(tokenUri, tokenRequest);
        
        if (!tokenResponse.IsSuccessStatusCode)
        {
            return null;
        }
        
        var tokenContent = await tokenResponse.Content.ReadAsStringAsync();
        var tokenData = JsonDocument.Parse(tokenContent);
        
        return tokenData.RootElement.GetProperty("access_token").GetString();
    }
    catch
    {
        return null;
    }
}

// Get Google service account access token with write permissions for creating spreadsheets
async Task<string?> GetServiceAccountAccessTokenWithWritePermissions(HttpClient httpClient, string clientEmail, string privateKey, string tokenUri)
{
    try
    {
        // Create JWT assertion
        var now = DateTimeOffset.UtcNow;
        var exp = now.AddHours(1);
        
        var header = new
        {
            alg = "RS256",
            typ = "JWT"
        };
        
        var payload = new
        {
            iss = clientEmail,
            scope = "https://www.googleapis.com/auth/spreadsheets https://www.googleapis.com/auth/drive.file",
            aud = tokenUri,
            exp = exp.ToUnixTimeSeconds(),
            iat = now.ToUnixTimeSeconds()
        };
        
        var headerJson = JsonSerializer.Serialize(header);
        var payloadJson = JsonSerializer.Serialize(payload);
        
        var headerEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(headerJson))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payloadEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        
        var message = $"{headerEncoded}.{payloadEncoded}";
        
        // Sign with RSA private key
        var signature = SignWithRSA(message, privateKey);
        var signatureEncoded = Convert.ToBase64String(signature)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        
        var jwt = $"{message}.{signatureEncoded}";
        
        // Exchange JWT for access token
        var tokenRequest = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
            new KeyValuePair<string, string>("assertion", jwt)
        });
        
        var tokenResponse = await httpClient.PostAsync(tokenUri, tokenRequest);
        
        if (!tokenResponse.IsSuccessStatusCode)
        {
            var errorContent = await tokenResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"[ERROR] Token request failed (write perms): {tokenResponse.StatusCode} - {errorContent}");
            return null;
        }
        
        var tokenContent = await tokenResponse.Content.ReadAsStringAsync();
        var tokenData = JsonDocument.Parse(tokenContent);
        
        return tokenData.RootElement.GetProperty("access_token").GetString();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERROR] GetServiceAccountAccessTokenWithWritePermissions failed: {ex.Message}");
        Console.WriteLine($"[ERROR] Stack trace: {ex.StackTrace}");
        return null;
    }
}

// Sign message with RSA private key
byte[] SignWithRSA(string message, string privateKeyPem)
{
    // Remove PEM headers and decode
    var privateKeyText = privateKeyPem
        .Replace("-----BEGIN PRIVATE KEY-----", "")
        .Replace("-----END PRIVATE KEY-----", "")
        .Replace("\n", "")
        .Replace("\r", "");
    
    var privateKeyBytes = Convert.FromBase64String(privateKeyText);
    
    using var rsa = RSA.Create();
    rsa.ImportPkcs8PrivateKey(privateKeyBytes, out _);
    
    var messageBytes = Encoding.UTF8.GetBytes(message);
    return rsa.SignData(messageBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
}

// Helper method to convert Google Sheets API response to records
List<Dictionary<string, object>> ConvertGoogleApiToRecords(JsonDocument sheetsData)
{
    var records = new List<Dictionary<string, object>>();
    
    if (!sheetsData.RootElement.TryGetProperty("values", out var valuesElement))
    {
        return records;
    }

    var values = valuesElement.EnumerateArray().ToList();
    if (values.Count == 0) return records;

    // First row as headers
    var headers = values[0].EnumerateArray().Select(cell => cell.GetString() ?? "").ToList();
    
    // Convert remaining rows to records
    for (int i = 1; i < values.Count; i++)
    {
        var row = values[i].EnumerateArray().ToList();
        var record = new Dictionary<string, object>();
        
        for (int j = 0; j < headers.Count; j++)
        {
            var value = j < row.Count ? (row[j].GetString() ?? "") : "";
            record[headers[j]] = value;
        }
        
        records.Add(record);
    }
    
    return records;
}

// Helper method to get descriptive HTTP error messages
string GetDescriptiveErrorMessage(System.Net.HttpStatusCode statusCode)
{
    return statusCode switch
    {
        System.Net.HttpStatusCode.NotFound => "Failed - Page not found (404 - the page does not exist)",
        System.Net.HttpStatusCode.Forbidden => "Failed - Access forbidden (403 - server refuses to grant access)",
        System.Net.HttpStatusCode.Unauthorized => "Failed - Unauthorized (401 - authentication required)",
        System.Net.HttpStatusCode.MethodNotAllowed => "Failed - Method not allowed (405 - server doesn't allow this request type)",
        System.Net.HttpStatusCode.InternalServerError => "Failed - Server error (500 - internal server malfunction)",
        System.Net.HttpStatusCode.BadGateway => "Failed - Bad gateway (502 - upstream server error)",
        System.Net.HttpStatusCode.ServiceUnavailable => "Failed - Service unavailable (503 - server temporarily overloaded)",
        System.Net.HttpStatusCode.GatewayTimeout => "Failed - Gateway timeout (504 - upstream server took too long)",
        System.Net.HttpStatusCode.RequestTimeout => "Failed - Request timeout (408 - server gave up waiting)",
        System.Net.HttpStatusCode.TooManyRequests => "Failed - Too many requests (429 - rate limit exceeded)",
        System.Net.HttpStatusCode.Gone => "Failed - Resource gone (410 - page permanently removed)",
        System.Net.HttpStatusCode.MovedPermanently => "Failed - Moved permanently (301 - page relocated)",
        System.Net.HttpStatusCode.Found => "Failed - Redirect (302 - page temporarily moved)",
        _ => $"Failed - HTTP {(int)statusCode} ({statusCode})"
    };
}

// Helper method to detect parked pages
async Task<bool> IsParkedPage(HttpClient httpClient, string url, string htmlContent)
{
    try
    {
        if (string.IsNullOrEmpty(htmlContent))
        {
            return false;
        }
        
        var content = htmlContent.ToLower();
        var domain = new Uri(url).Host.ToLower();
        
        // Common parked page indicators - more selective criteria
        var parkedPageIndicators = new[]
        {
            "this domain is for sale",
            "domain for sale",
            "buy this domain",
            "purchase this domain",
            "parked domain",
            "parked by godaddy",
            "parked by namecheap",
            "domain parking service",
            "this domain may be for sale",
            "expired domain",
            "domain expired",
            "suspended domain",
            "domain suspended",
            "godaddy parked page",
            "namecheap parked page",
            "sedo domain parking",
            "underconstruction.page",
            "account suspended",
            "hosting account suspended"
        };
        
        // Check for common parked page text - must be very explicit
        var explicitParkedIndicators = 0;
        foreach (var indicator in parkedPageIndicators)
        {
            if (content.Contains(indicator))
            {
                explicitParkedIndicators++;
            }
        }
        
        // Only flag as parked if we find explicit parking indicators
        if (explicitParkedIndicators > 0)
        {
            return true;
        }
        
        // Check for very specific parked page title patterns only
        if (content.Contains("<title>") && content.Contains("</title>"))
        {
            var titleStart = content.IndexOf("<title>") + 7;
            var titleEnd = content.IndexOf("</title>", titleStart);
            if (titleEnd > titleStart)
            {
                var title = content.Substring(titleStart, titleEnd - titleStart).Trim().ToLower();
                
                // Only flag very obvious parked page titles
                if ((title.Contains("domain") && title.Contains("sale")) ||
                    (title.Contains("parked") && title.Contains("domain")) ||
                    (title.Contains("expired") && title.Contains("domain")) ||
                    (title == domain) || // Title is just the domain name
                    (title.Length < 10 && title.Contains("parked")))
                {
                    return true;
                }
            }
        }
        
        // Remove the minimal content check as it's too aggressive
        // Many legitimate sites can have simple pages
        
        // Check for common parked page domains in redirects or content - more specific
        var commonParkedDomains = new[]
        {
            "sedo.com/search",
            "sedoparking.com",
            "parkingcrew.net",
            "bodis.com",
            "above.com/parking",
            "fabulous.com/parking",
            "parklogic.com",
            "namedrive.com",
            "trafficz.com"
        };
        
        foreach (var parkedDomain in commonParkedDomains)
        {
            if (content.Contains(parkedDomain))
            {
                return true;
            }
        }
        
        return false;
    }
    catch
    {
        return false; // If we can't analyze, assume it's not parked
    }
}

// Helper method to get descriptive network error messages
string GetDescriptiveNetworkError(HttpRequestException ex, string url)
{
    var message = ex.Message.ToLower();
    var domain = new Uri(url).Host;
    
    // Check for DNS resolution errors
    if (message.Contains("name or service not known") || 
        message.Contains("no such host") || 
        message.Contains("nodename nor servname provided") ||
        message.Contains("dns") ||
        message.Contains("name resolution"))
    {
        return $"Failed - Domain '{domain}' does not exist (DNS lookup failed)";
    }
    
    // Check for connection errors
    if (message.Contains("connection refused") || message.Contains("refused"))
    {
        return $"Failed - Connection refused (server at '{domain}' is not accepting connections)";
    }
    
    if (message.Contains("connection timed out") || message.Contains("timeout"))
    {
        return $"Failed - Connection timeout ('{domain}' took too long to respond)";
    }
    
    if (message.Contains("network is unreachable") || message.Contains("unreachable"))
    {
        return $"Failed - Network unreachable (cannot route to '{domain}')";
    }
    
    if (message.Contains("connection reset") || message.Contains("reset"))
    {
        return $"Failed - Connection reset ('{domain}' closed the connection unexpectedly)";
    }
    
    if (message.Contains("ssl") || message.Contains("tls") || message.Contains("certificate"))
    {
        return $"Failed - SSL/TLS error (security certificate issue with '{domain}')";
    }
    
    if (message.Contains("proxy"))
    {
        return "Failed - Proxy error (network proxy configuration issue)";
    }
    
    // Generic network error
    return $"Failed - Network error (cannot connect to '{domain}')";
}

// Helper method to log API performance when tracing is enabled
void LogApiPerformance(IConfiguration configuration, string apiCall, long durationMs)
{
    var apiPerformanceTrace = configuration.GetValue<bool>("ApiPerformanceTrace");
    if (apiPerformanceTrace)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff UTC");
        Console.WriteLine($"[API TRACE] {timestamp} | {apiCall} | Duration: {durationMs}ms");
    }
}

// ================================
// ARCHIVE URLS HELPER FUNCTIONS
// ================================

// Helper function to setup Google Sheets client
async Task<(bool success, HttpClient? client, string? sheetName, string? error)> SetupGoogleSheetsClient(
    IHttpClientFactory httpClientFactory, IConfiguration configuration, string spreadsheetId, int? gid)
{
    try
    {
        // Get service account configuration
        var serviceAccountSection = configuration.GetSection("GoogleSheets:ServiceAccount");
        var clientEmail = serviceAccountSection["client_email"];
        var privateKey = serviceAccountSection["private_key"];
        var tokenUri = serviceAccountSection["token_uri"];

        if (string.IsNullOrEmpty(clientEmail) || string.IsNullOrEmpty(privateKey) || string.IsNullOrEmpty(tokenUri))
        {
            return (false, null, null, "Service account configuration is missing");
        }

        var httpClient = httpClientFactory.CreateClient();
        
        // Get access token with write permissions for updating the sheet
        var accessToken = await GetServiceAccountAccessTokenWithWritePermissions(httpClient, clientEmail, privateKey, tokenUri);
        
        if (string.IsNullOrEmpty(accessToken))
        {
            return (false, null, null, "Failed to authenticate with Google Sheets API");
        }

        httpClient.DefaultRequestHeaders.Clear();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

        // Get the spreadsheet metadata to find the correct sheet name
        var metadataUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}";
        var metadataResponse = await httpClient.GetAsync(metadataUrl);
        
        if (!metadataResponse.IsSuccessStatusCode)
        {
            var metadataError = await metadataResponse.Content.ReadAsStringAsync();
            return (false, null, null, $"Error getting spreadsheet metadata: {metadataError}");
        }

        var metadataContent = await metadataResponse.Content.ReadAsStringAsync();
        var metadataDoc = JsonDocument.Parse(metadataContent);
        
        // Find the sheet name by GID or use the first sheet
        string sheetName = "Sheet1"; // Default fallback
        var sheets = metadataDoc.RootElement.GetProperty("sheets").EnumerateArray();
        
        foreach (var sheet in sheets)
        {
            var properties = sheet.GetProperty("properties");
            var sheetId = properties.GetProperty("sheetId").GetInt32();
            var title = properties.GetProperty("title").GetString() ?? "Sheet1";
            
            if (gid.HasValue && sheetId == gid.Value)
            {
                sheetName = title;
                break;
            }
            else if (!gid.HasValue && sheet.Equals(sheets.First()))
            {
                sheetName = title;
                break;
            }
        }

        return (true, httpClient, sheetName, null);
    }
    catch (Exception ex)
    {
        return (false, null, null, $"Exception during setup: {ex.Message}");
    }
}

// Helper function to get sheet data and prepare columns
async Task<(bool success, List<JsonElement>? values, int urlColumnIndex, int archiveStatusColumnIndex, int archiveUrlColumnIndex, string? error)> 
PrepareSheetData(HttpClient httpClient, string spreadsheetId, string sheetName, int? gid, IConfiguration configuration)
{
    try
    {
        // Get the sheet data
        var apiUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}/values/{Uri.EscapeDataString(sheetName)}";
        
        var sheetDataStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var response = await httpClient.GetAsync(apiUrl);
        sheetDataStopwatch.Stop();
        LogApiPerformance(configuration, $"Google Sheets Data API: GET {apiUrl}", sheetDataStopwatch.ElapsedMilliseconds);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            return (false, null, -1, -1, -1, $"Google Sheets API error: {errorContent}");
        }

        var responseContent = await response.Content.ReadAsStringAsync();
        var sheetsData = JsonDocument.Parse(responseContent);
        
        if (!sheetsData.RootElement.TryGetProperty("values", out var valuesElement))
        {
            return (false, null, -1, -1, -1, "No data found in the sheet");
        }

        var values = valuesElement.EnumerateArray().ToList();
        if (values.Count == 0)
        {
            return (false, null, -1, -1, -1, "No data found in the sheet");
        }

        // First row as headers
        var headers = values[0].EnumerateArray().Select(cell => cell.GetString() ?? "").ToList();
        
        // Find URL, Archive Status, and Archive URL column indices
        var urlColumnIndex = -1;
        var archiveStatusColumnIndex = -1;
        var archiveUrlColumnIndex = -1;
        
        for (int i = 0; i < headers.Count; i++)
        {
            var header = headers[i].ToLower();
            if (header.Contains("url") && !header.Contains("archive"))
            {
                urlColumnIndex = i;
            }
            else if (header.Contains("archive") && header.Contains("status"))
            {
                archiveStatusColumnIndex = i;
            }
            else if (header.Contains("archive") && header.Contains("url"))
            {
                archiveUrlColumnIndex = i;
            }
        }

        if (urlColumnIndex == -1)
        {
            return (false, null, -1, -1, -1, "No URL column found in the sheet");
        }

        // Add missing Archive Status and Archive URL columns automatically
        // Archive Status should be positioned immediately before Archive URL
        if (archiveStatusColumnIndex == -1 || archiveUrlColumnIndex == -1)
        {
            var requests = new List<object>();
            var valueUpdates = new List<object>();
            
            // If Archive URL exists but Archive Status doesn't, we need to INSERT a column before Archive URL
            if (archiveUrlColumnIndex != -1 && archiveStatusColumnIndex == -1)
            {
                Console.WriteLine($"Inserting Archive Status column before existing Archive URL column at index {archiveUrlColumnIndex}");
                
                // Insert a new column before the Archive URL column
                requests.Add(new {
                    insertDimension = new {
                        range = new {
                            sheetId = gid ?? 0,
                            dimension = "COLUMNS",
                            startIndex = archiveUrlColumnIndex,
                            endIndex = archiveUrlColumnIndex + 1
                        },
                        inheritFromBefore = false
                    }
                });
                
                // Set the header for the new column
                archiveStatusColumnIndex = archiveUrlColumnIndex;
                archiveUrlColumnIndex = archiveUrlColumnIndex + 1; // Archive URL shifts right
                
                var statusColumnLetter = GetColumnLetter(archiveStatusColumnIndex);
                valueUpdates.Add(new {
                    range = $"{sheetName}!{statusColumnLetter}1",
                    values = new[] { new[] { "Archive Status" } }
                });
            }
            // If neither exists, add both at the end
            else if (archiveStatusColumnIndex == -1 && archiveUrlColumnIndex == -1)
            {
                archiveStatusColumnIndex = headers.Count;
                archiveUrlColumnIndex = headers.Count + 1;
                
                Console.WriteLine($"Adding Archive Status at index {archiveStatusColumnIndex} and Archive URL at index {archiveUrlColumnIndex}");
                
                var statusColumnLetter = GetColumnLetter(archiveStatusColumnIndex);
                var urlColumnLetter = GetColumnLetter(archiveUrlColumnIndex);
                
                valueUpdates.Add(new {
                    range = $"{sheetName}!{statusColumnLetter}1",
                    values = new[] { new[] { "Archive Status" } }
                });
                
                valueUpdates.Add(new {
                    range = $"{sheetName}!{urlColumnLetter}1",
                    values = new[] { new[] { "Archive URL" } }
                });
            }
            // If Archive Status exists but Archive URL doesn't, add Archive URL after it
            else if (archiveStatusColumnIndex != -1 && archiveUrlColumnIndex == -1)
            {
                archiveUrlColumnIndex = archiveStatusColumnIndex + 1;
                
                Console.WriteLine($"Adding Archive URL column after Archive Status at index {archiveUrlColumnIndex}");
                
                var urlColumnLetter = GetColumnLetter(archiveUrlColumnIndex);
                valueUpdates.Add(new {
                    range = $"{sheetName}!{urlColumnLetter}1",
                    values = new[] { new[] { "Archive URL" } }
                });
            }
            
            // Add additional Auto-Archiver result columns after Archive URL if they don't exist
            var additionalColumns = new[] { 
                "Archive Date", "Upload Timestamp", "Upload Title", "Text Content", 
                "Screenshot", "Hash", "WACZ", "ReplayWebpage" 
            };
            
            var nextColumnIndex = Math.Max(archiveUrlColumnIndex + 1, headers.Count);
            
            foreach (var columnName in additionalColumns)
            {
                // Check if column already exists
                var existingIndex = headers.FindIndex(h => h.Equals(columnName, StringComparison.OrdinalIgnoreCase));
                if (existingIndex == -1)
                {
                    var columnLetter = GetColumnLetter(nextColumnIndex);
                    valueUpdates.Add(new {
                        range = $"{sheetName}!{columnLetter}1",
                        values = new[] { new[] { columnName } }
                    });
                    Console.WriteLine($"Adding {columnName} column at index {nextColumnIndex}");
                    nextColumnIndex++;
                }
            }
            
            // Execute column insertion requests first (if any)
            if (requests.Count > 0)
            {
                var batchUpdateRequest = new {
                    requests = requests
                };

                var batchUpdateUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}:batchUpdate";
                
                var insertStopwatch = System.Diagnostics.Stopwatch.StartNew();
                var insertResponse = await httpClient.PostAsync(
                    batchUpdateUrl,
                    new StringContent(JsonSerializer.Serialize(batchUpdateRequest), Encoding.UTF8, "application/json")
                );
                insertStopwatch.Stop();
                LogApiPerformance(configuration, $"Google Sheets Insert Column API: POST {batchUpdateUrl}", insertStopwatch.ElapsedMilliseconds);

                if (!insertResponse.IsSuccessStatusCode)
                {
                    var insertError = await insertResponse.Content.ReadAsStringAsync();
                    return (false, null, -1, -1, -1, $"Failed to insert Archive Status column: {insertError}");
                }
                
                Console.WriteLine($"Successfully inserted Archive Status column");
            }
            
            // Execute value updates (set headers)
            if (valueUpdates.Count > 0)
            {
                var valueUpdateRequest = new
                {
                    valueInputOption = "RAW",
                    data = valueUpdates
                };

                var valueUpdateUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}/values:batchUpdate";
                
                var headerStopwatch = System.Diagnostics.Stopwatch.StartNew();
                var headerResponse = await httpClient.PostAsync(
                    valueUpdateUrl,
                    new StringContent(JsonSerializer.Serialize(valueUpdateRequest), Encoding.UTF8, "application/json")
                );
                headerStopwatch.Stop();
                LogApiPerformance(configuration, $"Google Sheets Set Headers API: POST {valueUpdateUrl}", headerStopwatch.ElapsedMilliseconds);

                if (!headerResponse.IsSuccessStatusCode)
                {
                    var headerError = await headerResponse.Content.ReadAsStringAsync();
                    return (false, null, -1, -1, -1, $"Failed to set column headers: {headerError}");
                }
                
                Console.WriteLine($"Successfully set headers: Archive Status at column {GetColumnLetter(archiveStatusColumnIndex)}, Archive URL at column {GetColumnLetter(archiveUrlColumnIndex)}");
            }
        }

        return (true, values, urlColumnIndex, archiveStatusColumnIndex, archiveUrlColumnIndex, null);
    }
    catch (Exception ex)
    {
        return (false, null, -1, -1, -1, $"Exception preparing sheet data: {ex.Message}");
    }
}

// Helper function to process a single URL for archiving
async Task<(string cellValue, bool isError)> ProcessSingleUrlForArchiving(
    string urlValue, HttpClient waybackClient, IConfiguration configuration, bool preValidationEnabled)
{
    try
    {
        // Auto-prefix URLs that don't have http/https
        if (!urlValue.StartsWith("http://") && !urlValue.StartsWith("https://"))
        {
            if (urlValue.Contains(".") && !urlValue.Contains(" "))
            {
                urlValue = "http://" + urlValue;
                Console.WriteLine($"Auto-prefixed URL with http://: {urlValue}");
            }
        }

        // Validate URL format
        if (!Uri.TryCreate(urlValue, UriKind.Absolute, out var validatedUri) || 
            (validatedUri.Scheme != "http" && validatedUri.Scheme != "https"))
        {
            return ("Failed - Invalid URL format", true);
        }

        // Check if pre-validation is enabled
        if (preValidationEnabled)
        {
            // Perform basic pre-validation
            try
            {
                var testClient = new HttpClient();
                testClient.Timeout = TimeSpan.FromSeconds(10);
                testClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                
                var testResponse = await testClient.GetAsync(urlValue);
                
                if (!testResponse.IsSuccessStatusCode)
                {
                    if (testResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        return ("Failed - URL not found (404)", true);
                    }
                    else if (testResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        return ("Failed - Access forbidden (403)", true);
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                return (GetDescriptiveNetworkError(ex, urlValue), true);
            }
            catch (TaskCanceledException)
            {
                return ("Failed - Connection timeout during validation", true);
            }
        }

        // Archive URL with Wayback Machine
        Console.WriteLine($"Archiving URL with Wayback Machine: {urlValue}");
        var waybackSaveUrl = $"https://web.archive.org/save/{Uri.EscapeDataString(urlValue)}";
        
        var waybackSaveStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var waybackResponse = await waybackClient.PostAsync(waybackSaveUrl, null);
        waybackSaveStopwatch.Stop();
        LogApiPerformance(configuration, $"Wayback Machine Save API: POST {waybackSaveUrl}", waybackSaveStopwatch.ElapsedMilliseconds);
            
        Console.WriteLine($"Wayback API response status for {urlValue}: {waybackResponse.StatusCode}");
        
        if (!waybackResponse.IsSuccessStatusCode)
        {
            return ($"Failed - HTTP {(int)waybackResponse.StatusCode}", true);
        }

        // Try to get the archive URL from response headers
        string? archiveUrl = null;
        
        if (waybackResponse.Headers.TryGetValues("Content-Location", out var contentLocationValues))
        {
            var contentLocation = contentLocationValues.First();
            archiveUrl = contentLocation.StartsWith("/web/") 
                ? $"https://web.archive.org{contentLocation}"
                : contentLocation;
        }
        else if (waybackResponse.Headers.Location != null)
        {
            archiveUrl = waybackResponse.Headers.Location.ToString();
        }

        if (!string.IsNullOrEmpty(archiveUrl))
        {
            Console.WriteLine($"Archive success for {urlValue}: {archiveUrl}");
            return (archiveUrl, false);
        }
        else
        {
            // Use fallback URL format
            var currentTimestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            archiveUrl = $"https://web.archive.org/web/{currentTimestamp}/{urlValue}";
            Console.WriteLine($"Archive success (fallback URL) for {urlValue}: {archiveUrl}");
            return (archiveUrl, false);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error archiving URL {urlValue}: {ex.Message}");
        return ("Failed - Exception during archiving", true);
    }
}

// Helper function to update Google Sheets with results
async Task<bool> UpdateSheetWithResults(HttpClient httpClient, string spreadsheetId, 
    List<object> valueUpdates, List<object> formatUpdates, IConfiguration configuration)
{
    try
    {
        // Batch update values
        if (valueUpdates.Count > 0)
        {
            var batchUpdateRequest = new
            {
                valueInputOption = "RAW",
                data = valueUpdates
            };

            var batchUpdateUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}/values:batchUpdate";
            
            var batchUpdateStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var updateResponse = await httpClient.PostAsync(
                batchUpdateUrl,
                new StringContent(JsonSerializer.Serialize(batchUpdateRequest), Encoding.UTF8, "application/json")
            );
            batchUpdateStopwatch.Stop();
            LogApiPerformance(configuration, $"Google Sheets Batch Update API: POST {batchUpdateUrl} (updating {valueUpdates.Count} cells)", batchUpdateStopwatch.ElapsedMilliseconds);

            if (!updateResponse.IsSuccessStatusCode)
            {
                var updateError = await updateResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"Error updating Google Sheet: {updateError}");
                return false;
            }
        }

        // Apply formatting
        if (formatUpdates.Count > 0)
        {
            var formatRequest = new
            {
                requests = formatUpdates.Select(update => new {
                    repeatCell = new {
                        range = ((dynamic)update).range,
                        cell = new {
                            userEnteredFormat = ((dynamic)update).format
                        },
                        fields = "userEnteredFormat.textFormat.foregroundColor"
                    }
                })
            };

            var formatUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}:batchUpdate";
            
            var formatStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var formatResponse = await httpClient.PostAsync(
                formatUrl,
                new StringContent(JsonSerializer.Serialize(formatRequest), Encoding.UTF8, "application/json")
            );
            formatStopwatch.Stop();
            LogApiPerformance(configuration, $"Google Sheets Format API: POST {formatUrl} (formatting {formatUpdates.Count} cells)", formatStopwatch.ElapsedMilliseconds);

            if (!formatResponse.IsSuccessStatusCode)
            {
                var formatError = await formatResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"Warning: Failed to apply red formatting: {formatError}");
            }
        }

        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Exception during sheet update: {ex.Message}");
        return false;
    }
}

// Helper function to convert column index to Excel-style column letter
string GetColumnLetter(int columnIndex)
{
    string columnLetter = "";
    while (columnIndex >= 0)
    {
        columnLetter = (char)('A' + (columnIndex % 26)) + columnLetter;
        columnIndex = (columnIndex / 26) - 1;
    }
    return columnLetter;
}

// ================================
// PARALLEL BATCH PROCESSING FUNCTIONS
// ================================

// Helper function to process URLs in parallel batches
async Task<List<UrlProcessingJob>> ProcessUrlBatchesInParallel(
    List<JsonElement> values, 
    int urlColumnIndex, 
    int archiveStatusColumnIndex,
    int archiveUrlColumnIndex, 
    IHttpClientFactory httpClientFactory, 
    IConfiguration configuration,
    bool preValidationEnabled)
{
    var urlJobs = new List<UrlProcessingJob>();
    var processedResults = new List<UrlProcessingJob>();
    
    // Extract URLs that need processing
    for (int i = 1; i < values.Count; i++)
    {
        var row = values[i].EnumerateArray().ToList();
        var urlValue = urlColumnIndex < row.Count ? (row[urlColumnIndex].GetString() ?? "").Trim() : "";
        var archiveUrlValue = archiveUrlColumnIndex < row.Count ? (row[archiveUrlColumnIndex].GetString() ?? "").Trim() : "";
        
        // Skip if no URL or already has Archive URL
        if (string.IsNullOrEmpty(urlValue) || !string.IsNullOrEmpty(archiveUrlValue))
        {
            continue;
        }
        
        urlJobs.Add(new UrlProcessingJob 
        { 
            RowIndex = i, 
            UrlValue = urlValue 
        });
    }
    
    if (urlJobs.Count == 0)
    {
        return processedResults;
    }
    
    // Configuration for batch processing
    var batchSize = configuration.GetValue<int>("ArchiveUrlsBatchSize", 10);
    var maxConcurrency = configuration.GetValue<int>("ArchiveUrlsMaxConcurrency", 5);
    
    Console.WriteLine($"Processing {urlJobs.Count} URLs in batches of {batchSize} with max concurrency of {maxConcurrency}");
    
    // Create semaphore for rate limiting
    using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    
    // Split into batches and process in parallel
    var batches = urlJobs
        .Select((job, index) => new { job, index })
        .GroupBy(x => x.index / batchSize)
        .Select(g => g.Select(x => x.job).ToList())
        .ToList();
    
    var batchTasks = batches.Select(async (batch, batchIndex) =>
    {
        await semaphore.WaitAsync();
        try
        {
            Console.WriteLine($"Starting batch {batchIndex + 1}/{batches.Count} with {batch.Count} URLs");
            
            // Process URLs in this batch concurrently
            var batchResults = await Task.WhenAll(batch.Select(async job =>
            {
                // Create a dedicated HTTP client for each concurrent task
                var waybackClient = httpClientFactory.CreateClient();
                waybackClient.DefaultRequestHeaders.Clear();
                waybackClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                waybackClient.Timeout = TimeSpan.FromSeconds(30);
                
                Console.WriteLine($"Row {job.RowIndex + 1}: Processing URL: {job.UrlValue}");
                
                var (cellValue, isError) = await ProcessSingleUrlForArchiving(job.UrlValue, waybackClient, configuration, preValidationEnabled);
                
                return new UrlProcessingJob
                {
                    RowIndex = job.RowIndex,
                    UrlValue = job.UrlValue,
                    CellValue = cellValue,
                    IsError = isError
                };
            }));
            
            Console.WriteLine($"Completed batch {batchIndex + 1}/{batches.Count}");
            return batchResults;
        }
        finally
        {
            semaphore.Release();
        }
    });
    
    var allBatchResults = await Task.WhenAll(batchTasks);
    processedResults = allBatchResults.SelectMany(batch => batch).ToList();
    
    Console.WriteLine($"Parallel processing complete. Processed {processedResults.Count} URLs");
    return processedResults;
}

// Helper function to collect and update Google Sheets with batch results
async Task<bool> UpdateSheetWithBatchResults(
    HttpClient httpClient, 
    string spreadsheetId, 
    string sheetName, 
    int archiveStatusColumnIndex,
    int archiveUrlColumnIndex, 
    List<UrlProcessingJob> results, 
    int? gid, 
    IConfiguration configuration)
{
    var valueUpdates = new List<object>();
    var formatUpdates = new List<object>();
    
    foreach (var result in results)
    {
        if (result.IsError)
        {
            // Write error message to Archive Status column
            var archiveStatusCellRange = $"{sheetName}!{GetColumnLetter(archiveStatusColumnIndex)}{result.RowIndex + 1}";
            
            valueUpdates.Add(new {
                range = archiveStatusCellRange,
                values = new[] { new[] { result.CellValue } }
            });
            
            // Add red formatting for Archive Status column
            formatUpdates.Add(new {
                range = new {
                    sheetId = gid ?? 0,
                    startRowIndex = result.RowIndex,
                    endRowIndex = result.RowIndex + 1,
                    startColumnIndex = archiveStatusColumnIndex,
                    endColumnIndex = archiveStatusColumnIndex + 1
                },
                format = new {
                    textFormat = new {
                        foregroundColor = new {
                            red = 1.0,
                            green = 0.0,
                            blue = 0.0
                        }
                    }
                }
            });
            
            // Leave Archive URL column empty for errors (no update needed)
        }
        else
        {
            // Write archive URL to Archive URL column
            var archiveUrlCellRange = $"{sheetName}!{GetColumnLetter(archiveUrlColumnIndex)}{result.RowIndex + 1}";
            
            valueUpdates.Add(new {
                range = archiveUrlCellRange,
                values = new[] { new[] { result.CellValue } }
            });
            
            // Write "Success" to Archive Status column
            var archiveStatusCellRange = $"{sheetName}!{GetColumnLetter(archiveStatusColumnIndex)}{result.RowIndex + 1}";
            
            valueUpdates.Add(new {
                range = archiveStatusCellRange,
                values = new[] { new[] { "Success" } }
            });
            
            // Add black formatting for both columns (success)
            formatUpdates.Add(new {
                range = new {
                    sheetId = gid ?? 0,
                    startRowIndex = result.RowIndex,
                    endRowIndex = result.RowIndex + 1,
                    startColumnIndex = archiveStatusColumnIndex,
                    endColumnIndex = archiveUrlColumnIndex + 1
                },
                format = new {
                    textFormat = new {
                        foregroundColor = new {
                            red = 0.0,
                            green = 0.0,
                            blue = 0.0
                        }
                    }
                }
            });
        }
    }
    
    return await UpdateSheetWithResults(httpClient, spreadsheetId, valueUpdates, formatUpdates, configuration);
}

app.MapMethods("/google-sheets/archive-urls", new[] { "GET", "POST" }, async (HttpContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration) =>
{
    try
    {
        var url = context.Request.Query["url"];
        
        if (string.IsNullOrEmpty(url))
        {
            return Results.BadRequest(new { message = "URL parameter is required" });
        }

        // Read preValidation parameter from query string (default is false)
        var preValidationParam = context.Request.Query["preValidation"].ToString().ToLower();
        bool preValidationEnabled = preValidationParam == "true" || preValidationParam == "1";

        // Extract spreadsheet ID and GID from Google Sheets URL
        var (spreadsheetId, gid) = ExtractSpreadsheetInfo(url!);
        
        if (string.IsNullOrEmpty(spreadsheetId))
        {
            return Results.BadRequest(new { 
                message = "Invalid Google Sheets URL. Expected format: https://docs.google.com/spreadsheets/d/{SPREADSHEET_ID}/edit#gid={SHEET_ID}",
                providedUrl = url.ToString()
            });
        }

        // Setup Google Sheets client and get sheet metadata
        var (setupSuccess, httpClient, sheetName, setupError) = await SetupGoogleSheetsClient(httpClientFactory, configuration, spreadsheetId, gid);
        if (!setupSuccess)
        {
            return Results.Problem(setupError, statusCode: 500);
        }

        // Prepare sheet data and column indices
        var (dataSuccess, values, urlColumnIndex, archiveStatusColumnIndex, archiveUrlColumnIndex, dataError) = await PrepareSheetData(httpClient!, spreadsheetId, sheetName!, gid, configuration);
        if (!dataSuccess)
        {
            if (dataError!.Contains("No data found"))
            {
                return Results.Ok(new { message = "No data found in the sheet", totalRecords = 0, archivedCount = 0 });
            }
            return Results.Problem(dataError, statusCode: 500);
        }

        // Process URLs for archiving using parallel batch processing
        int totalRecords = values!.Count - 1; // Excluding header row
        
        var results = await ProcessUrlBatchesInParallel(values, urlColumnIndex, archiveStatusColumnIndex, archiveUrlColumnIndex, httpClientFactory, configuration, preValidationEnabled);
        
        int processedCount = results.Count;
        int archivedCount = results.Count(r => !r.IsError);

        // Update Google Sheet with batch results
        var updateSuccess = await UpdateSheetWithBatchResults(httpClient!, spreadsheetId, sheetName!, archiveStatusColumnIndex, archiveUrlColumnIndex, results, gid, configuration);
        if (!updateSuccess)
        {
            return Results.Problem("Failed to update Google Sheet with archive results", statusCode: 500);
        }

        return Results.Ok(new { 
            message = processedCount > 0 
                ? $"Successfully processed {processedCount} URLs and archived {archivedCount} of them"
                : "No new URLs found to archive",
            totalRecords = totalRecords,
            processedCount = processedCount,
            archivedCount = archivedCount,
            failedCount = processedCount - archivedCount,
            skippedCount = totalRecords - processedCount,
            successRate = processedCount > 0 ? Math.Round((double)archivedCount / processedCount * 100, 1) : 0,
            estimatedTimePerUrl = "~2 seconds",
            spreadsheetId = spreadsheetId,
            sheetName = sheetName,
            sourceUrl = url.ToString(),
            columnsUpdated = new[] { "Archive Status", "Archive URL" },
            note = "Archive Status shows 'Success' or error messages in red. Archive URL contains the archived link for successful archives."
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error processing archive URLs: {ex.Message}", statusCode: 500);
    }
});

// INTEGRATION INSTRUCTIONS:
// Copy this endpoint code and paste it into your Program.cs file alongside your existing endpoints
// This endpoint uses your existing helper functions: ExtractSpreadsheetInfo() and GetServiceAccountAccessToken()

app.MapGet("/google-sheets/check-permissions", async (HttpContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration) =>
{
    try
    {
        var url = context.Request.Query["url"];
        var checkWriteParam = context.Request.Query["checkWrite"];
        var checkWrite = !string.IsNullOrEmpty(checkWriteParam) && checkWriteParam.ToString().ToLower() == "true";

        if (string.IsNullOrEmpty(url))
        {
            return Results.BadRequest(new
            {
                hasPermission = false,
                message = "URL parameter is required"
            });
        }

        // Extract spreadsheet ID and GID from Google Sheets URL
        var (spreadsheetId, gid) = ExtractSpreadsheetInfo(url!);

        if (string.IsNullOrEmpty(spreadsheetId))
        {
            return Results.BadRequest(new
            {
                hasPermission = false,
                message = "Invalid Google Sheets URL. Expected format: https://docs.google.com/spreadsheets/d/{SPREADSHEET_ID}/edit#gid={SHEET_ID}",
                providedUrl = url.ToString()
            });
        }

        // Get service account configuration
        var serviceAccountSection = configuration.GetSection("GoogleSheets:ServiceAccount");
        var clientEmail = serviceAccountSection["client_email"];
        var privateKey = serviceAccountSection["private_key"];
        var tokenUri = serviceAccountSection["token_uri"];

        if (string.IsNullOrEmpty(clientEmail) || string.IsNullOrEmpty(privateKey) || string.IsNullOrEmpty(tokenUri))
        {
            Console.WriteLine($"[DEBUG] Missing service account config - clientEmail: {!string.IsNullOrEmpty(clientEmail)}, privateKey: {!string.IsNullOrEmpty(privateKey)}, tokenUri: {!string.IsNullOrEmpty(tokenUri)}");
            return Results.BadRequest(new
            {
                hasPermission = false,
                message = "Service account configuration is missing",
                debug = new
                {
                    hasClientEmail = !string.IsNullOrEmpty(clientEmail),
                    hasPrivateKey = !string.IsNullOrEmpty(privateKey),
                    hasTokenUri = !string.IsNullOrEmpty(tokenUri),
                    privateKeyLength = privateKey?.Length ?? 0
                }
            });
        }

        Console.WriteLine($"[DEBUG] Service account config loaded - email: {clientEmail}, privateKey length: {privateKey.Length}, tokenUri: {tokenUri}");

        var httpClient = httpClientFactory.CreateClient();

        // Choose the appropriate access token based on what we're testing
        var accessToken = checkWrite 
            ? await GetServiceAccountAccessTokenWithWritePermissions(httpClient, clientEmail, privateKey, tokenUri)
            : await GetServiceAccountAccessToken(httpClient, clientEmail, privateKey, tokenUri);

        if (string.IsNullOrEmpty(accessToken))
        {
            Console.WriteLine($"[ERROR] Failed to get access token for service account: {clientEmail}");
            return Results.Json(new
            {
                hasPermission = false,
                message = "Failed to authenticate with Google Sheets API - check service account credentials in App Settings",
                error = "authentication_failed",
                debug = new
                {
                    clientEmail = clientEmail,
                    privateKeyLength = privateKey?.Length ?? 0,
                    hasTokenUri = !string.IsNullOrEmpty(tokenUri)
                }
            }, statusCode: 401);
        }

        httpClient.DefaultRequestHeaders.Clear();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

        // Try to get the spreadsheet metadata - this is a lightweight way to check permissions
        var metadataUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}";
        var metadataResponse = await httpClient.GetAsync(metadataUrl);

        if (metadataResponse.IsSuccessStatusCode)
        {
            // If we can read metadata, we have at least viewer permission
            // Let's also try to read a small amount of data to confirm read access
            try
            {
                var metadataContent = await metadataResponse.Content.ReadAsStringAsync();
                var metadataDoc = JsonDocument.Parse(metadataContent);

                // Find the correct sheet name using the same logic as your other endpoints
                string sheetName = "Sheet1"; // Default fallback
                var sheets = metadataDoc.RootElement.GetProperty("sheets").EnumerateArray();

                foreach (var sheet in sheets)
                {
                    var properties = sheet.GetProperty("properties");
                    var sheetId = properties.GetProperty("sheetId").GetInt32();
                    var title = properties.GetProperty("title").GetString() ?? "Sheet1";

                    if (gid.HasValue && sheetId == gid.Value)
                    {
                        sheetName = title;
                        break;
                    }
                    else if (!gid.HasValue && sheet.Equals(sheets.First()))
                    {
                        // Use first sheet if no GID specified
                        sheetName = title;
                        break;
                    }
                }

                // Try to read just the header row to verify read permissions
                var testReadUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}/values/{Uri.EscapeDataString(sheetName)}!A1:Z1";
                var testReadResponse = await httpClient.GetAsync(testReadUrl);

                if (testReadResponse.IsSuccessStatusCode)
                {
                    string permissionLevel = "read_confirmed";
                    
                    // If checking write permissions, attempt a non-destructive write test
                    if (checkWrite)
                    {
                        try
                        {
                            // Test write permission by attempting to get cell formatting (requires write scope but doesn't modify data)
                            var formatTestUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}?fields=sheets.data.rowData.values.userEnteredFormat&ranges={Uri.EscapeDataString(sheetName)}!A1:A1";
                            var formatTestResponse = await httpClient.GetAsync(formatTestUrl);
                            
                            if (formatTestResponse.IsSuccessStatusCode)
                            {
                                permissionLevel = "write_confirmed";
                            }
                            else if (formatTestResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
                            {
                                permissionLevel = "read_only";
                            }
                            else
                            {
                                permissionLevel = "write_unknown";
                            }
                        }
                        catch
                        {
                            permissionLevel = "write_test_failed";
                        }
                    }

                    return Results.Ok(new
                    {
                        hasPermission = true,
                        message = checkWrite 
                            ? $"Service account has {(permissionLevel.Contains("write") ? "write" : "read-only")} access to the Google Sheet"
                            : "Service account has read access to the Google Sheet",
                        spreadsheetId = spreadsheetId,
                        sheetName = sheetName,
                        permissions = permissionLevel,
                        serviceAccountEmail = clientEmail,
                        sourceUrl = url.ToString(),
                        checkedWrite = checkWrite
                    });
                }
                else
                {
                    // Can read metadata but not sheet data - unusual case
                    return Results.Json(new
                    {
                        hasPermission = false,
                        message = "Service account can access spreadsheet metadata but cannot read sheet data",
                        error = "partial_access",
                        statusCode = (int)testReadResponse.StatusCode
                    }, statusCode: 403);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    hasPermission = false,
                    message = $"Error verifying sheet access: {ex.Message}",
                    error = "verification_failed"
                }, statusCode: 500);
            }
        }
        else if (metadataResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            // 403 means we don't have permission to access this spreadsheet
            return Results.Json(new
            {
                hasPermission = false,
                message = "Service account does not have permission to access this Google Sheet. Please share the sheet with the service account email.",
                error = "insufficient_permissions",
                serviceAccountEmail = clientEmail,
                spreadsheetId = spreadsheetId,
                sourceUrl = url.ToString()
            }, statusCode: 403);
        }
        else if (metadataResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // 404 means the spreadsheet doesn't exist or URL is invalid
            return Results.Json(new
            {
                hasPermission = false,
                message = "Google Sheet not found. Please check that the URL is correct and the sheet exists.",
                error = "spreadsheet_not_found",
                spreadsheetId = spreadsheetId,
                sourceUrl = url.ToString()
            }, statusCode: 404);
        }
        else
        {
            // Other error
            var errorContent = await metadataResponse.Content.ReadAsStringAsync();
            return Results.Json(new
            {
                hasPermission = false,
                message = $"Google Sheets API error: {errorContent}",
                error = "api_error",
                statusCode = (int)metadataResponse.StatusCode
            }, statusCode: (int)metadataResponse.StatusCode);
        }
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            hasPermission = false,
            message = $"Error checking permissions: {ex.Message}",
            error = "internal_error"
        }, statusCode: 500);
    }
});

app.MapPost("/google-sheets/extract-domains", async (HttpContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration) =>
{
    try
    {
        var url = context.Request.Query["url"];
        
        if (string.IsNullOrEmpty(url))
        {
            return Results.BadRequest(new { message = "URL parameter is required" });
        }

        // Extract spreadsheet ID and GID from Google Sheets URL
        var (spreadsheetId, gid) = ExtractSpreadsheetInfo(url!);
        
        if (string.IsNullOrEmpty(spreadsheetId))
        {
            return Results.BadRequest(new { 
                message = "Invalid Google Sheets URL. Expected format: https://docs.google.com/spreadsheets/d/{SPREADSHEET_ID}/edit#gid={SHEET_ID}",
                providedUrl = url.ToString()
            });
        }
        
        // Get service account configuration
        var serviceAccountSection = configuration.GetSection("GoogleSheets:ServiceAccount");
        var clientEmail = serviceAccountSection["client_email"];
        var privateKey = serviceAccountSection["private_key"];
        var tokenUri = serviceAccountSection["token_uri"];

        if (string.IsNullOrEmpty(clientEmail) || string.IsNullOrEmpty(privateKey) || string.IsNullOrEmpty(tokenUri))
        {
            return Results.BadRequest(new { message = "Service account configuration is missing" });
        }

        var httpClient = httpClientFactory.CreateClient();
        
        // Get access token with write permissions for updating the sheet
        var accessToken = await GetServiceAccountAccessTokenWithWritePermissions(httpClient, clientEmail, privateKey, tokenUri);
        
        if (string.IsNullOrEmpty(accessToken))
        {
            return Results.Problem("Failed to authenticate with Google Sheets API", statusCode: 401);
        }

        httpClient.DefaultRequestHeaders.Clear();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

        // First, get the spreadsheet metadata to find the correct sheet name
        var metadataUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}";
        var metadataResponse = await httpClient.GetAsync(metadataUrl);
        
        if (!metadataResponse.IsSuccessStatusCode)
        {
            var metadataError = await metadataResponse.Content.ReadAsStringAsync();
            return Results.Problem($"Error getting spreadsheet metadata: {metadataError}", statusCode: (int)metadataResponse.StatusCode);
        }

        var metadataContent = await metadataResponse.Content.ReadAsStringAsync();
        var metadataDoc = JsonDocument.Parse(metadataContent);
        
        // Find the sheet name by GID or use the first sheet
        string sheetName = "Sheet1"; // Default fallback
        var sheets = metadataDoc.RootElement.GetProperty("sheets").EnumerateArray();
        
        foreach (var sheet in sheets)
        {
            var properties = sheet.GetProperty("properties");
            var sheetId = properties.GetProperty("sheetId").GetInt32();
            var title = properties.GetProperty("title").GetString() ?? "Sheet1";
            
            if (gid.HasValue && sheetId == gid.Value)
            {
                sheetName = title;
                break;
            }
            else if (!gid.HasValue && sheet.Equals(sheets.First()))
            {
                // Use first sheet if no GID specified
                sheetName = title;
                break;
            }
        }

        // Get the sheet data
        var apiUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}/values/{Uri.EscapeDataString(sheetName)}";
        
        var response = await httpClient.GetAsync(apiUrl);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            return Results.Problem($"Google Sheets API error: {errorContent}", statusCode: (int)response.StatusCode);
        }

        var responseContent = await response.Content.ReadAsStringAsync();
        var sheetsData = JsonDocument.Parse(responseContent);
        
        if (!sheetsData.RootElement.TryGetProperty("values", out var valuesElement))
        {
            return Results.Ok(new { 
                message = "No data found in the sheet",
                totalRecords = 0,
                processedCount = 0
            });
        }

        var values = valuesElement.EnumerateArray().ToList();
        if (values.Count == 0)
        {
            return Results.Ok(new { 
                message = "No data found in the sheet",
                totalRecords = 0,
                processedCount = 0
            });
        }

        // First row as headers
        var headers = values[0].EnumerateArray().Select(cell => cell.GetString() ?? "").ToList();
        
        // Find URL, Domain, and Archive URL column indices
        var urlColumnIndex = -1;
        var domainColumnIndex = -1;
        var archiveUrlColumnIndex = -1;
        
        for (int i = 0; i < headers.Count; i++)
        {
            var header = headers[i].ToLower();
            if (header.Contains("url") && !header.Contains("archive") && !header.Contains("domain"))
            {
                urlColumnIndex = i;
            }
            else if (header.Contains("domain"))
            {
                domainColumnIndex = i;
            }
            else if (header.Contains("archive") && header.Contains("url"))
            {
                archiveUrlColumnIndex = i;
            }
        }

        if (urlColumnIndex == -1)
        {
            return Results.BadRequest(new { 
                message = "No URL column found in the sheet",
                headers = headers,
                debug = "Looking for a column containing 'url' but not 'archive' or 'domain'"
            });
        }

        // Add missing Domain column automatically
        if (domainColumnIndex == -1)
        {
            // Insert Domain column after URL column but before Archive URL column
            if (archiveUrlColumnIndex != -1)
            {
                // Insert new column at the Archive URL position, which will shift Archive URL to the right
                domainColumnIndex = archiveUrlColumnIndex;
                
                Console.WriteLine($"Inserting Domain column at position {domainColumnIndex} (before Archive URL column)...");
                
                // First, insert a new column using batchUpdate to shift existing columns
                var insertColumnRequest = new
                {
                    requests = new[]
                    {
                        new
                        {
                            insertDimension = new
                            {
                                range = new
                                {
                                    sheetId = gid ?? 0,
                                    dimension = "COLUMNS",
                                    startIndex = domainColumnIndex,
                                    endIndex = domainColumnIndex + 1
                                },
                                inheritFromBefore = false
                            }
                        }
                    }
                };

                var insertColumnUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}:batchUpdate";
                var insertColumnResponse = await httpClient.PostAsync(
                    insertColumnUrl,
                    new StringContent(JsonSerializer.Serialize(insertColumnRequest), Encoding.UTF8, "application/json")
                );

                if (!insertColumnResponse.IsSuccessStatusCode)
                {
                    var insertError = await insertColumnResponse.Content.ReadAsStringAsync();
                    return Results.Problem($"Failed to insert Domain column: {insertError}", statusCode: (int)insertColumnResponse.StatusCode);
                }
                
                Console.WriteLine($"Successfully inserted new column at position {domainColumnIndex}");
                
                // Update the Archive URL column index since it was shifted to the right
                archiveUrlColumnIndex++;
                
                // Now add the "Domain" header to the newly inserted column
                var columnLetter = GetColumnLetter(domainColumnIndex);
                var addHeaderRequest = new
                {
                    range = $"{sheetName}!{columnLetter}1",
                    values = new[] { new[] { "Domain" } }
                };

                var addHeaderUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}/values/{Uri.EscapeDataString($"{sheetName}!{columnLetter}1")}?valueInputOption=RAW";
                var addHeaderResponse = await httpClient.PutAsync(
                    addHeaderUrl,
                    new StringContent(JsonSerializer.Serialize(addHeaderRequest), Encoding.UTF8, "application/json")
                );

                if (!addHeaderResponse.IsSuccessStatusCode)
                {
                    var headerError = await addHeaderResponse.Content.ReadAsStringAsync();
                    return Results.Problem($"Failed to add Domain column header: {headerError}", statusCode: (int)addHeaderResponse.StatusCode);
                }
                
                Console.WriteLine($"Successfully added Domain header at column {columnLetter} (index {domainColumnIndex})");
            }
            else
            {
                // No Archive URL column found, add Domain column after URL column
                domainColumnIndex = urlColumnIndex + 1;
                
                // Check if this position already has content
                if (domainColumnIndex < headers.Count)
                {
                    // Insert a new column at this position
                    Console.WriteLine($"Inserting Domain column at position {domainColumnIndex} (after URL column)...");
                    
                    var insertColumnRequest = new
                    {
                        requests = new[]
                        {
                            new
                            {
                                insertDimension = new
                                {
                                    range = new
                                    {
                                        sheetId = gid ?? 0,
                                        dimension = "COLUMNS",
                                        startIndex = domainColumnIndex,
                                        endIndex = domainColumnIndex + 1
                                    },
                                    inheritFromBefore = false
                                }
                            }
                        }
                    };

                    var insertColumnUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}:batchUpdate";
                    var insertColumnResponse = await httpClient.PostAsync(
                        insertColumnUrl,
                        new StringContent(JsonSerializer.Serialize(insertColumnRequest), Encoding.UTF8, "application/json")
                    );

                    if (!insertColumnResponse.IsSuccessStatusCode)
                    {
                        var insertError = await insertColumnResponse.Content.ReadAsStringAsync();
                        return Results.Problem($"Failed to insert Domain column: {insertError}", statusCode: (int)insertColumnResponse.StatusCode);
                    }
                }
                else
                {
                    // Add at the end - no need to insert a new column
                    domainColumnIndex = headers.Count;
                }
                
                // Add the "Domain" header
                var columnLetter = GetColumnLetter(domainColumnIndex);
                var addHeaderRequest = new
                {
                    range = $"{sheetName}!{columnLetter}1",
                    values = new[] { new[] { "Domain" } }
                };

                var addHeaderUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}/values/{Uri.EscapeDataString($"{sheetName}!{columnLetter}1")}?valueInputOption=RAW";
                var addHeaderResponse = await httpClient.PutAsync(
                    addHeaderUrl,
                    new StringContent(JsonSerializer.Serialize(addHeaderRequest), Encoding.UTF8, "application/json")
                );

                if (!addHeaderResponse.IsSuccessStatusCode)
                {
                    var headerError = await addHeaderResponse.Content.ReadAsStringAsync();
                    return Results.Problem($"Failed to add Domain column header: {headerError}", statusCode: (int)addHeaderResponse.StatusCode);
                }
                
                Console.WriteLine($"Successfully added Domain column at index {domainColumnIndex} (column {columnLetter})");
            }
            
            // Update our local headers array to reflect the new structure
            headers.Insert(domainColumnIndex, "Domain");
            
            // Re-read the sheet data to get the updated structure with the new Domain column
            Console.WriteLine("Re-reading sheet data after column insertion...");
            var refreshResponse = await httpClient.GetAsync(apiUrl);
            
            if (!refreshResponse.IsSuccessStatusCode)
            {
                var refreshError = await refreshResponse.Content.ReadAsStringAsync();
                return Results.Problem($"Error re-reading sheet data: {refreshError}", statusCode: (int)refreshResponse.StatusCode);
            }

            var refreshContent = await refreshResponse.Content.ReadAsStringAsync();
            var refreshSheetsData = JsonDocument.Parse(refreshContent);
            
            if (refreshSheetsData.RootElement.TryGetProperty("values", out var refreshValuesElement))
            {
                values = refreshValuesElement.EnumerateArray().ToList();
                Console.WriteLine($"Successfully re-read sheet data with {values.Count} rows");
            }
        }

        int totalRecords = values.Count - 1; // Excluding header row
        int processedCount = 0; // Records that had URLs and needed domain extraction
        int extractedCount = 0;
        var valueUpdates = new List<object>();
        var formatUpdates = new List<object>(); // For red text formatting on errors

        // Helper function to extract top-level domain
        string ExtractTopLevelDomain(string urlString)
        {
            try
            {
                // Auto-prefix URLs that don't have http/https
                if (!urlString.StartsWith("http://") && !urlString.StartsWith("https://"))
                {
                    if (urlString.Contains(".") && !urlString.Contains(" "))
                    {
                        urlString = "http://" + urlString;
                    }
                }

                if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri))
                {
                    return "Invalid URL format";
                }

                var host = uri.Host.ToLower();
                
                // Remove 'www.' prefix if present
                if (host.StartsWith("www."))
                {
                    host = host.Substring(4);
                }

                // For simple cases, extract the domain
                var parts = host.Split('.');
                if (parts.Length < 2)
                {
                    return "Invalid domain";
                }

                // Handle common TLD patterns
                if (parts.Length == 2)
                {
                    // Simple case: example.com
                    return host;
                }
                else if (parts.Length >= 3)
                {
                    // Check for common country code TLDs with second-level domains
                    var lastPart = parts[^1]; // Last part (TLD)
                    var secondLastPart = parts[^2]; // Second to last part
                    
                    // Common country codes with second-level domains
                    var countryCodesWithSLD = new HashSet<string> 
                    {
                        "co.uk", "co.jp", "co.kr", "co.nz", "co.za", "co.in", "co.il",
                        "com.au", "com.br", "com.mx", "com.ar", "com.tr", "com.sg",
                        "ac.uk", "org.uk", "net.uk", "gov.uk", "edu.au", "gov.au"
                    };
                    
                    var possibleSLD = $"{secondLastPart}.{lastPart}";
                    
                    if (countryCodesWithSLD.Contains(possibleSLD))
                    {
                        // Return domain.co.uk format
                        if (parts.Length >= 3)
                        {
                            return $"{parts[^3]}.{possibleSLD}";
                        }
                    }
                    
                    // Default case: return last two parts as domain.tld
                    return $"{secondLastPart}.{lastPart}";
                }

                return host;
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        // Process each record (skip header row)
        for (int i = 1; i < values.Count; i++)
        {
            var row = values[i].EnumerateArray().ToList();
            
            // Get URL value
            var urlValue = urlColumnIndex < row.Count ? (row[urlColumnIndex].GetString() ?? "").Trim() : "";
            
            // Get Domain value
            var domainValue = domainColumnIndex < row.Count ? (row[domainColumnIndex].GetString() ?? "").Trim() : "";
            
            // Skip if no URL or already has Domain (indicating it was already processed)
            if (string.IsNullOrEmpty(urlValue))
            {
                Console.WriteLine($"Row {i + 1}: Skipping empty URL");
                continue;
            }
            
            if (!string.IsNullOrEmpty(domainValue))
            {
                Console.WriteLine($"Row {i + 1}: Skipping already processed URL: {urlValue}");
                continue;
            }

            Console.WriteLine($"Row {i + 1}: Processing URL: {urlValue}");

            // Count this as a processed record (valid URL that needs domain extraction)
            processedCount++;

            try
            {
                // Extract the top-level domain
                var extractedDomain = ExtractTopLevelDomain(urlValue);
                
                // Determine if this is an error or success
                bool isError = extractedDomain.StartsWith("Invalid") || extractedDomain.StartsWith("Error:");
                
                if (!isError)
                {
                    extractedCount++;
                    Console.WriteLine($"Row {i + 1}: Extracted domain '{extractedDomain}' from URL: {urlValue}");
                }
                else
                {
                    Console.WriteLine($"Row {i + 1}: Failed to extract domain from URL: {urlValue} - {extractedDomain}");
                }

                // Prepare update for Domain column
                var domainCellRange = $"{sheetName}!{GetColumnLetter(domainColumnIndex)}{i + 1}";
                
                valueUpdates.Add(new {
                    range = domainCellRange,
                    values = new[] { new[] { extractedDomain } }
                });
                
                // Add formatting (red for errors, black for success)
                var rowIndex = i; // 0-based row index
                var colIndex = domainColumnIndex; // 0-based column index
                
                formatUpdates.Add(new {
                    range = new {
                        sheetId = gid ?? 0,
                        startRowIndex = rowIndex,
                        endRowIndex = rowIndex + 1,
                        startColumnIndex = colIndex,
                        endColumnIndex = colIndex + 1
                    },
                    format = new {
                        textFormat = new {
                            foregroundColor = new {
                                red = isError ? 1.0 : 0.0,
                                green = 0.0,
                                blue = 0.0
                            }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                // Log the error and update Domain column with error message
                Console.WriteLine($"Error extracting domain from URL {urlValue}: {ex.Message}");
                
                var domainCellRange = $"{sheetName}!{GetColumnLetter(domainColumnIndex)}{i + 1}";
                valueUpdates.Add(new {
                    range = domainCellRange,
                    values = new[] { new[] { $"Error: Exception during extraction" } }
                });
                
                // Add red formatting for exception error
                var rowIndex = i;
                var colIndex = domainColumnIndex;
                
                formatUpdates.Add(new {
                    range = new {
                        sheetId = gid ?? 0,
                        startRowIndex = rowIndex,
                        endRowIndex = rowIndex + 1,
                        startColumnIndex = colIndex,
                        endColumnIndex = colIndex + 1
                    },
                    format = new {
                        textFormat = new {
                            foregroundColor = new {
                                red = 1.0,
                                green = 0.0,
                                blue = 0.0
                            }
                        }
                    }
                });
            }
        }

        // Batch update the Google Sheet with all domain extractions
        if (valueUpdates.Count > 0)
        {
            var batchUpdateRequest = new
            {
                valueInputOption = "RAW",
                data = valueUpdates
            };

            var batchUpdateUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}/values:batchUpdate";
            var updateResponse = await httpClient.PostAsync(
                batchUpdateUrl,
                new StringContent(JsonSerializer.Serialize(batchUpdateRequest), Encoding.UTF8, "application/json")
            );

            if (!updateResponse.IsSuccessStatusCode)
            {
                var updateError = await updateResponse.Content.ReadAsStringAsync();
                return Results.Problem($"Error updating Google Sheet: {updateError}", statusCode: (int)updateResponse.StatusCode);
            }
        }

        // Apply formatting (red for errors, black for success)
        if (formatUpdates.Count > 0)
        {
            var formatRequest = new
            {
                requests = formatUpdates.Select(update => new {
                    repeatCell = new {
                        range = ((dynamic)update).range,
                        cell = new {
                            userEnteredFormat = ((dynamic)update).format
                        },
                        fields = "userEnteredFormat.textFormat.foregroundColor"
                    }
                })
            };

            var formatUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}:batchUpdate";
            var formatResponse = await httpClient.PostAsync(
                formatUrl,
                new StringContent(JsonSerializer.Serialize(formatRequest), Encoding.UTF8, "application/json")
            );

            if (!formatResponse.IsSuccessStatusCode)
            {
                // Don't fail the entire operation if formatting fails, just log it
                var formatError = await formatResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"Warning: Failed to apply formatting to domain extraction results: {formatError}");
            }
        }

        return Results.Ok(new { 
            message = processedCount > 0 
                ? $"Successfully processed {processedCount} URLs and extracted domains for {extractedCount} of them"
                : "No new URLs found to process",
            totalRecords = totalRecords,
            processedCount = processedCount,
            extractedCount = extractedCount,
            failedCount = processedCount - extractedCount,
            skippedCount = totalRecords - processedCount,
            successRate = processedCount > 0 ? Math.Round((double)extractedCount / processedCount * 100, 1) : 0,
            spreadsheetId = spreadsheetId,
            sheetName = sheetName,
            sourceUrl = url.ToString(),
            columnsUpdated = new[] { "Domain" },
            note = "Domain extractions are shown in normal text for success, red text for errors. Blank cells indicate URLs that could not be processed."
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error processing domain extraction: {ex.Message}", statusCode: 500);
    }
});

app.MapPost("/google-sheets/extract-channels", async (HttpContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration) =>
{
    try
    {
        var url = context.Request.Query["url"];
        
        if (string.IsNullOrEmpty(url))
        {
            return Results.BadRequest(new { message = "URL parameter is required" });
        }

        // Extract spreadsheet ID and GID from Google Sheets URL
        var (spreadsheetId, gid) = ExtractSpreadsheetInfo(url!);
        
        if (string.IsNullOrEmpty(spreadsheetId))
        {
            return Results.BadRequest(new { 
                message = "Invalid Google Sheets URL. Expected format: https://docs.google.com/spreadsheets/d/{SPREADSHEET_ID}/edit#gid={SHEET_ID}",
                providedUrl = url.ToString()
            });
        }
        
        // Get service account configuration
        var serviceAccountSection = configuration.GetSection("GoogleSheets:ServiceAccount");
        var clientEmail = serviceAccountSection["client_email"];
        var privateKey = serviceAccountSection["private_key"];
        var tokenUri = serviceAccountSection["token_uri"];

        if (string.IsNullOrEmpty(clientEmail) || string.IsNullOrEmpty(privateKey) || string.IsNullOrEmpty(tokenUri))
        {
            return Results.BadRequest(new { message = "Service account configuration is missing" });
        }

        var httpClient = httpClientFactory.CreateClient();
        
        // Get access token with write permissions for updating the sheet
        var accessToken = await GetServiceAccountAccessTokenWithWritePermissions(httpClient, clientEmail, privateKey, tokenUri);
        
        if (string.IsNullOrEmpty(accessToken))
        {
            return Results.Problem("Failed to authenticate with Google Sheets API", statusCode: 401);
        }

        httpClient.DefaultRequestHeaders.Clear();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

        // First, get the spreadsheet metadata to find the correct sheet name
        var metadataUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}";
        var metadataResponse = await httpClient.GetAsync(metadataUrl);
        
        if (!metadataResponse.IsSuccessStatusCode)
        {
            var metadataError = await metadataResponse.Content.ReadAsStringAsync();
            return Results.Problem($"Error getting spreadsheet metadata: {metadataError}", statusCode: (int)metadataResponse.StatusCode);
        }

        var metadataContent = await metadataResponse.Content.ReadAsStringAsync();
        var metadataDoc = JsonDocument.Parse(metadataContent);
        
        // Find the sheet name by GID or use the first sheet
        string sheetName = "Sheet1"; // Default fallback
        var sheets = metadataDoc.RootElement.GetProperty("sheets").EnumerateArray();
        
        foreach (var sheet in sheets)
        {
            var properties = sheet.GetProperty("properties");
            var sheetId = properties.GetProperty("sheetId").GetInt32();
            var title = properties.GetProperty("title").GetString() ?? "Sheet1";
            
            if (gid.HasValue && sheetId == gid.Value)
            {
                sheetName = title;
                break;
            }
            else if (!gid.HasValue && sheet.Equals(sheets.First()))
            {
                // Use first sheet if no GID specified
                sheetName = title;
                break;
            }
        }

        // Get the sheet data
        var apiUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}/values/{Uri.EscapeDataString(sheetName)}";
        
        var response = await httpClient.GetAsync(apiUrl);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            return Results.Problem($"Google Sheets API error: {errorContent}", statusCode: (int)response.StatusCode);
        }

        var responseContent = await response.Content.ReadAsStringAsync();
        var sheetsData = JsonDocument.Parse(responseContent);
        
        if (!sheetsData.RootElement.TryGetProperty("values", out var valuesElement))
        {
            return Results.Ok(new { 
                message = "No data found in the sheet",
                totalRecords = 0,
                processedCount = 0
            });
        }

        var values = valuesElement.EnumerateArray().ToList();
        if (values.Count == 0)
        {
            return Results.Ok(new { 
                message = "No data found in the sheet",
                totalRecords = 0,
                processedCount = 0
            });
        }

        // First row as headers
        var headers = values[0].EnumerateArray().Select(cell => cell.GetString() ?? "").ToList();
        
        // Find URL, Channel, and Archive URL column indices
        var urlColumnIndex = -1;
        var channelColumnIndex = -1;
        var archiveUrlColumnIndex = -1;
        
        for (int i = 0; i < headers.Count; i++)
        {
            var header = headers[i].ToLower();
            if (header.Contains("url") && !header.Contains("archive") && !header.Contains("channel"))
            {
                urlColumnIndex = i;
            }
            else if (header.Contains("channel"))
            {
                channelColumnIndex = i;
            }
            else if (header.Contains("archive") && header.Contains("url"))
            {
                archiveUrlColumnIndex = i;
            }
        }

        if (urlColumnIndex == -1)
        {
            return Results.BadRequest(new { 
                message = "No URL column found in the sheet",
                headers = headers,
                debug = "Looking for a column containing 'url' but not 'archive' or 'channel'"
            });
        }

        // Add missing Channel column automatically - position between URL and Archive URL
        if (channelColumnIndex == -1)
        {
            // Insert Channel column after URL column but before Archive URL column
            if (archiveUrlColumnIndex != -1)
            {
                // Insert new column at the Archive URL position, which will shift Archive URL to the right
                channelColumnIndex = archiveUrlColumnIndex;
                
                Console.WriteLine($"Inserting Channel column at position {channelColumnIndex} (before Archive URL column)...");
                
                // First, insert a new column using batchUpdate to shift existing columns
                var insertColumnRequest = new
                {
                    requests = new[]
                    {
                        new
                        {
                            insertDimension = new
                            {
                                range = new
                                {
                                    sheetId = gid ?? 0,
                                    dimension = "COLUMNS",
                                    startIndex = channelColumnIndex,
                                    endIndex = channelColumnIndex + 1
                                },
                                inheritFromBefore = false
                            }
                        }
                    }
                };

                var insertColumnUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}:batchUpdate";
                var insertColumnResponse = await httpClient.PostAsync(
                    insertColumnUrl,
                    new StringContent(JsonSerializer.Serialize(insertColumnRequest), Encoding.UTF8, "application/json")
                );

                if (!insertColumnResponse.IsSuccessStatusCode)
                {
                    var insertError = await insertColumnResponse.Content.ReadAsStringAsync();
                    return Results.Problem($"Failed to insert Channel column: {insertError}", statusCode: (int)insertColumnResponse.StatusCode);
                }
                
                Console.WriteLine($"Successfully inserted new column at position {channelColumnIndex}");
                
                // Update the Archive URL column index since it was shifted to the right
                archiveUrlColumnIndex++;
                
                // Now add the "Channel" header to the newly inserted column
                var columnLetter = GetColumnLetter(channelColumnIndex);
                var addHeaderRequest = new
                {
                    range = $"{sheetName}!{columnLetter}1",
                    values = new[] { new[] { "Channel" } }
                };

                var addHeaderUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}/values/{Uri.EscapeDataString($"{sheetName}!{columnLetter}1")}?valueInputOption=RAW";
                var addHeaderResponse = await httpClient.PutAsync(
                    addHeaderUrl,
                    new StringContent(JsonSerializer.Serialize(addHeaderRequest), Encoding.UTF8, "application/json")
                );

                if (!addHeaderResponse.IsSuccessStatusCode)
                {
                    var headerError = await addHeaderResponse.Content.ReadAsStringAsync();
                    return Results.Problem($"Failed to add Channel column header: {headerError}", statusCode: (int)addHeaderResponse.StatusCode);
                }
                
                Console.WriteLine($"Successfully added Channel header at column {columnLetter} (index {channelColumnIndex})");
            }
            else
            {
                // No Archive URL column found, add Channel column after URL column
                channelColumnIndex = urlColumnIndex + 1;
                
                // Check if this position already has content
                if (channelColumnIndex < headers.Count)
                {
                    // Insert a new column at this position
                    Console.WriteLine($"Inserting Channel column at position {channelColumnIndex} (after URL column)...");
                    
                    var insertColumnRequest = new
                    {
                        requests = new[]
                        {
                            new
                            {
                                insertDimension = new
                                {
                                    range = new
                                    {
                                        sheetId = gid ?? 0,
                                        dimension = "COLUMNS",
                                        startIndex = channelColumnIndex,
                                        endIndex = channelColumnIndex + 1
                                    },
                                    inheritFromBefore = false
                                }
                            }
                        }
                    };

                    var insertColumnUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}:batchUpdate";
                    var insertColumnResponse = await httpClient.PostAsync(
                        insertColumnUrl,
                        new StringContent(JsonSerializer.Serialize(insertColumnRequest), Encoding.UTF8, "application/json")
                    );

                    if (!insertColumnResponse.IsSuccessStatusCode)
                    {
                        var insertError = await insertColumnResponse.Content.ReadAsStringAsync();
                        return Results.Problem($"Failed to insert Channel column: {insertError}", statusCode: (int)insertColumnResponse.StatusCode);
                    }
                }
                else
                {
                    // Add at the end - no need to insert a new column
                    channelColumnIndex = headers.Count;
                }
                
                // Add the "Channel" header
                var columnLetter = GetColumnLetter(channelColumnIndex);
                var addHeaderRequest = new
                {
                    range = $"{sheetName}!{columnLetter}1",
                    values = new[] { new[] { "Channel" } }
                };

                var addHeaderUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}/values/{Uri.EscapeDataString($"{sheetName}!{columnLetter}1")}?valueInputOption=RAW";
                var addHeaderResponse = await httpClient.PutAsync(
                    addHeaderUrl,
                    new StringContent(JsonSerializer.Serialize(addHeaderRequest), Encoding.UTF8, "application/json")
                );

                if (!addHeaderResponse.IsSuccessStatusCode)
                {
                    var headerError = await addHeaderResponse.Content.ReadAsStringAsync();
                    return Results.Problem($"Failed to add Channel column header: {headerError}", statusCode: (int)addHeaderResponse.StatusCode);
                }
                
                Console.WriteLine($"Successfully added Channel column at index {channelColumnIndex} (column {columnLetter})");
            }
            
            // Update our local headers array to reflect the new structure
            headers.Insert(channelColumnIndex, "Channel");
            
            // Re-read the sheet data to get the updated structure with the new Channel column
            Console.WriteLine("Re-reading sheet data after column insertion...");
            var refreshResponse = await httpClient.GetAsync(apiUrl);
            
            if (!refreshResponse.IsSuccessStatusCode)
            {
                var refreshError = await refreshResponse.Content.ReadAsStringAsync();
                return Results.Problem($"Error re-reading sheet data: {refreshError}", statusCode: (int)refreshResponse.StatusCode);
            }

            var refreshContent = await refreshResponse.Content.ReadAsStringAsync();
            var refreshSheetsData = JsonDocument.Parse(refreshContent);
            
            if (refreshSheetsData.RootElement.TryGetProperty("values", out var refreshValuesElement))
            {
                values = refreshValuesElement.EnumerateArray().ToList();
                Console.WriteLine($"Successfully re-read sheet data with {values.Count} rows");
            }
        }

        int totalRecords = values.Count - 1; // Excluding header row
        int processedCount = 0; // Records that had URLs and needed channel extraction
        int extractedCount = 0;
        var valueUpdates = new List<object>();
        var formatUpdates = new List<object>(); // For red text formatting on errors

        // Helper function to extract channel (leftmost part up to first URL segment, with special Facebook handling)
        string ExtractChannel(string urlString)
        {
            try
            {
                // Auto-prefix URLs that don't have http/https
                if (!urlString.StartsWith("http://") && !urlString.StartsWith("https://"))
                {
                    if (urlString.Contains(".") && !urlString.Contains(" "))
                    {
                        urlString = "http://" + urlString;
                    }
                }

                if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri))
                {
                    return "Invalid URL format";
                }

                // Get the base URL with scheme and host
                var baseUrl = $"{uri.Scheme}://{uri.Host}";
                
                // Add port if it's not the default port
                if (!uri.IsDefaultPort)
                {
                    baseUrl += $":{uri.Port}";
                }

                // Get the path segments
                var segments = uri.Segments;
                
                // Check if first path section contains ".php" - if so, return full URL
                if (segments.Length > 1)
                {
                    var firstSegment = segments[1].TrimEnd('/');
                    if (firstSegment.Contains(".php"))
                    {
                        return urlString; // Return the full original URL
                    }
                }
                
                // Special handling for Facebook URLs
                if (uri.Host.ToLower().Contains("facebook.com"))
                {
                    if (segments.Length <= 1)
                    {
                        return baseUrl; // No path segments
                    }

                    var firstSegment = segments[1].TrimEnd('/').ToLower();
                    
                    // Facebook-specific path segment rules
                    var facebookSegmentRules = new Dictionary<string, int>
                    {
                        { "groups", 2 },
                        { "pages", 4 },
                        { "watch", 1 },
                        { "events", 2 },
                        { "profile.php", 1 },
                        { "permalink.php", 1 },
                        { "help", 0 },
                        { "settings", 0 },
                        { "notifications", 0 },
                        { "marketplace", 1 },
                        { "gaming", 0 },
                        { "business", 0 },
                        { "ads", 2 },
                        { "messages", 2 },
                        { "friends", 0 },
                        { "directory", 0 },
                        { "latest", 0 }
                    };

                    int segmentsToInclude = 1; // Default to 1 segment if no match
                    
                    if (facebookSegmentRules.TryGetValue(firstSegment, out var ruleSegments))
                    {
                        segmentsToInclude = ruleSegments;
                    }

                    // If rule says 0 segments, return empty (blank channel)
                    if (segmentsToInclude == 0)
                    {
                        return "";
                    }

                    // Build URL with the specified number of segments
                    for (int i = 1; i <= Math.Min(segmentsToInclude, segments.Length - 1); i++)
                    {
                        var segment = segments[i].TrimEnd('/');
                        // Add all segments, including empty ones (they'll become just "/")
                        baseUrl += "/" + segment;
                    }

                    return baseUrl;
                }
                else
                {
                    // Default behavior for non-Facebook URLs: include first path segment only
                    if (segments.Length > 1 && !string.IsNullOrEmpty(segments[1].Trim('/')))
                    {
                        // Add the first path segment (e.g., "/marianpy1")
                        var firstSegment = segments[1].TrimEnd('/');
                        baseUrl += "/" + firstSegment;
                    }

                    return baseUrl;
                }
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        // Process each record (skip header row)
        for (int i = 1; i < values.Count; i++)
        {
            var row = values[i].EnumerateArray().ToList();
            
            // Get URL value
            var urlValue = urlColumnIndex < row.Count ? (row[urlColumnIndex].GetString() ?? "").Trim() : "";
            
            // Get Channel value
            var channelValue = channelColumnIndex < row.Count ? (row[channelColumnIndex].GetString() ?? "").Trim() : "";
            
            // Skip if no URL or already has Channel (indicating it was already processed)
            if (string.IsNullOrEmpty(urlValue))
            {
                Console.WriteLine($"Row {i + 1}: Skipping empty URL");
                continue;
            }
            
            if (!string.IsNullOrEmpty(channelValue))
            {
                Console.WriteLine($"Row {i + 1}: Skipping already processed URL: {urlValue}");
                continue;
            }

            Console.WriteLine($"Row {i + 1}: Processing URL: {urlValue}");

            // Count this as a processed record (valid URL that needs channel extraction)
            processedCount++;

            try
            {
                // Extract the channel
                var extractedChannel = ExtractChannel(urlValue);
                
                // Determine if this is an error or success
                bool isError = extractedChannel.StartsWith("Invalid") || extractedChannel.StartsWith("Error:");
                
                if (!isError)
                {
                    extractedCount++;
                    Console.WriteLine($"Row {i + 1}: Extracted channel '{extractedChannel}' from URL: {urlValue}");
                }
                else
                {
                    Console.WriteLine($"Row {i + 1}: Failed to extract channel from URL: {urlValue} - {extractedChannel}");
                }

                // Prepare update for Channel column
                var channelCellRange = $"{sheetName}!{GetColumnLetter(channelColumnIndex)}{i + 1}";
                
                valueUpdates.Add(new {
                    range = channelCellRange,
                    values = new[] { new[] { extractedChannel } }
                });
                
                // Add formatting (red for errors, black for success)
                var rowIndex = i; // 0-based row index
                var colIndex = channelColumnIndex; // 0-based column index
                
                formatUpdates.Add(new {
                    range = new {
                        sheetId = gid ?? 0,
                        startRowIndex = rowIndex,
                        endRowIndex = rowIndex + 1,
                        startColumnIndex = colIndex,
                        endColumnIndex = colIndex + 1
                    },
                    format = new {
                        textFormat = new {
                            foregroundColor = new {
                                red = isError ? 1.0 : 0.0,
                                green = 0.0,
                                blue = 0.0
                            }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                // Log the error and update Channel column with error message
                Console.WriteLine($"Error extracting channel from URL {urlValue}: {ex.Message}");
                
                var channelCellRange = $"{sheetName}!{GetColumnLetter(channelColumnIndex)}{i + 1}";
                valueUpdates.Add(new {
                    range = channelCellRange,
                    values = new[] { new[] { $"Error: Exception during extraction" } }
                });
                
                // Add red formatting for exception error
                var rowIndex = i;
                var colIndex = channelColumnIndex;
                
                formatUpdates.Add(new {
                    range = new {
                        sheetId = gid ?? 0,
                        startRowIndex = rowIndex,
                        endRowIndex = rowIndex + 1,
                        startColumnIndex = colIndex,
                        endColumnIndex = colIndex + 1
                    },
                    format = new {
                        textFormat = new {
                            foregroundColor = new {
                                red = 1.0,
                                green = 0.0,
                                blue = 0.0
                            }
                        }
                    }
                });
            }
        }

        // Batch update the Google Sheet with all channel extractions
        if (valueUpdates.Count > 0)
        {
            var batchUpdateRequest = new
            {
                valueInputOption = "RAW",
                data = valueUpdates
            };

            var batchUpdateUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}/values:batchUpdate";
            var updateResponse = await httpClient.PostAsync(
                batchUpdateUrl,
                new StringContent(JsonSerializer.Serialize(batchUpdateRequest), Encoding.UTF8, "application/json")
            );

            if (!updateResponse.IsSuccessStatusCode)
            {
                var updateError = await updateResponse.Content.ReadAsStringAsync();
                return Results.Problem($"Error updating Google Sheet: {updateError}", statusCode: (int)updateResponse.StatusCode);
            }
        }

        // Apply formatting (red for errors, black for success)
        if (formatUpdates.Count > 0)
        {
            var formatRequest = new
            {
                requests = formatUpdates.Select(update => new {
                    repeatCell = new {
                        range = ((dynamic)update).range,
                        cell = new {
                            userEnteredFormat = ((dynamic)update).format
                        },
                        fields = "userEnteredFormat.textFormat.foregroundColor"
                    }
                })
            };

            var formatUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}:batchUpdate";
            var formatResponse = await httpClient.PostAsync(
                formatUrl,
                new StringContent(JsonSerializer.Serialize(formatRequest), Encoding.UTF8, "application/json")
            );

            if (!formatResponse.IsSuccessStatusCode)
            {
                // Don't fail the entire operation if formatting fails, just log it
                var formatError = await formatResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"Warning: Failed to apply formatting to channel extraction results: {formatError}");
            }
        }

        return Results.Ok(new { 
            message = processedCount > 0 
                ? $"Successfully processed {processedCount} URLs and extracted channels for {extractedCount} of them"
                : "No new URLs found to process",
            totalRecords = totalRecords,
            processedCount = processedCount,
            extractedCount = extractedCount,
            failedCount = processedCount - extractedCount,
            skippedCount = totalRecords - processedCount,
            successRate = processedCount > 0 ? Math.Round((double)extractedCount / processedCount * 100, 1) : 0,
            spreadsheetId = spreadsheetId,
            sheetName = sheetName,
            sourceUrl = url.ToString(),
            columnsUpdated = new[] { "Channel" },
            note = "Channel extractions are shown in normal text for success, red text for errors. Blank cells indicate URLs that could not be processed."
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error processing channel extraction: {ex.Message}", statusCode: 500);
    }
});

// ================================
// BELLINGCAT AUTO-ARCHIVER INTEGRATION
// ================================

// Helper function to create and monitor ACI in background
async Task CreateAndMonitorACI(
    string jobId,
    string spreadsheetId,
    IConfiguration configuration)
{
    try
    {
        var subscriptionId = configuration["AzureContainerInstance:SubscriptionId"];
        var resourceGroupName = configuration["AzureContainerInstance:ResourceGroupName"];
        var location = configuration["AzureContainerInstance:Location"] ?? "australiacentral";
        var containerImage = configuration["AzureContainerInstance:ContainerImage"] ?? "bellingcat/auto-archiver:latest";
        var cpuCores = double.Parse(configuration["AzureContainerInstance:CpuCores"] ?? "1.0");
        var memoryInGB = double.Parse(configuration["AzureContainerInstance:MemoryInGB"] ?? "1.5");
        
        // Azure Container Registry credentials (for private registry)
        var registryServer = configuration["AzureContainerInstance:RegistryServer"];
        var registryUsername = configuration["AzureContainerInstance:RegistryUsername"];
        var registryPassword = configuration["AzureContainerInstance:RegistryPassword"];
        
        // Azure Storage for volumes
        var storageAccountName = configuration["AzureStorage:AccountName"];
        var storageAccountKey = configuration["AzureStorage:AccountKey"];
        var fileShareName = configuration["AzureStorage:FileShareName"] ?? "auto-archiver-share";
        
        if (string.IsNullOrEmpty(subscriptionId) || string.IsNullOrEmpty(resourceGroupName))
        {
            if (activeJobs.TryGetValue(jobId, out var job))
            {
                job.Status = "failed";
                job.EndTime = DateTime.UtcNow;
                job.ErrorMessage = "Azure Container Instance configuration is missing (SubscriptionId or ResourceGroupName)";
            }
            return;
        }
        
        if (string.IsNullOrEmpty(storageAccountName) || string.IsNullOrEmpty(storageAccountKey))
        {
            if (activeJobs.TryGetValue(jobId, out var job))
            {
                job.Status = "failed";
                job.EndTime = DateTime.UtcNow;
                job.ErrorMessage = "Azure Storage configuration is missing (AccountName or AccountKey)";
            }
            return;
        }
        
        // Authenticate using DefaultAzureCredential (works with managed identity in production, Azure CLI locally)
        var credential = new DefaultAzureCredential();
        var armClient = new ArmClient(credential);
        
        // Get subscription and resource group
        var subscriptionResourceId = new Azure.Core.ResourceIdentifier($"/subscriptions/{subscriptionId}");
        var subscription = await armClient.GetSubscriptionResource(subscriptionResourceId).GetAsync();
        var resourceGroup = await subscription.Value.GetResourceGroupAsync(resourceGroupName);
        
        // Generate unique container group name
        var containerGroupName = $"auto-archiver-{jobId}";
        
        // Use Azure REST API to create container with Log Analytics support
        // The .NET SDK doesn't expose Log Analytics configuration properties
        var workspaceId = configuration["AzureLogAnalytics:WorkspaceId"];
        var workspaceKey = configuration["AzureLogAnalytics:WorkspaceKey"];
        
        Console.WriteLine($"[DEBUG] Creating container via REST API with Log Analytics integration");
        
        // Build container creation request body
        var containerRequest = new
        {
            location = location,
            properties = new
            {
                containers = new[]
                {
                    new
                    {
                        name = "auto-archiver",
                        properties = new
                        {
                            image = containerImage,
                            command = new[]
                            {
                                "/entrypoint.sh",
                                "--config",
                                "/mnt/fileshare/secrets/orchestration.yaml",
                                "--feeders=gsheet_feeder_db",
                                $"--gsheet_feeder_db.sheet_id={spreadsheetId}",
                                
                                // S3 Storage credentials from App Settings
                                $"--s3_storage.bucket={configuration["AutoArchiver:S3Storage:Bucket"]}",
                                $"--s3_storage.region={configuration["AutoArchiver:S3Storage:Region"]}",
                                $"--s3_storage.key={configuration["AutoArchiver:S3Storage:Key"]}",
                                $"--s3_storage.secret={configuration["AutoArchiver:S3Storage:Secret"]}",
                                $"--s3_storage.endpoint_url={configuration["AutoArchiver:S3Storage:EndpointUrl"]}",
                                $"--s3_storage.cdn_url={configuration["AutoArchiver:S3Storage:CdnUrl"]}",
                                
                                // Google Drive Storage credentials from App Settings
                                $"--gdrive_storage.root_folder_id={configuration["AutoArchiver:GDriveStorage:RootFolderId"]}",
                                
                                // Wayback Machine Enricher credentials from App Settings
                                $"--wayback_extractor_enricher.key={configuration["AutoArchiver:WaybackEnricher:Key"]}",
                                $"--wayback_extractor_enricher.secret={configuration["AutoArchiver:WaybackEnricher:Secret"]}"
                            },
                            resources = new
                            {
                                requests = new
                                {
                                    cpu = cpuCores,
                                    memoryInGB = memoryInGB
                                }
                            },
                            environmentVariables = new object[]
                            {
                                new { name = "GOOGLE_PROJECT_ID", value = configuration["GoogleSheets:ServiceAccount:project_id"] },
                                new { name = "GOOGLE_CLIENT_EMAIL", value = configuration["GoogleSheets:ServiceAccount:client_email"] },
                                new { name = "GOOGLE_CLIENT_ID", value = configuration["GoogleSheets:ServiceAccount:client_id"] },
                                new { name = "GOOGLE_CLIENT_CERT_URL", value = configuration["GoogleSheets:ServiceAccount:client_x509_cert_url"] },
                                new { name = "GOOGLE_PRIVATE_KEY_ID", secureValue = configuration["GoogleSheets:ServiceAccount:private_key_id"] },
                                new { name = "GOOGLE_PRIVATE_KEY", secureValue = configuration["GoogleSheets:ServiceAccount:private_key"] }
                            },
                            volumeMounts = new[]
                            {
                                new
                                {
                                    name = "auto-archiver-storage",
                                    mountPath = "/mnt/fileshare",
                                    readOnly = false
                                }
                            }
                        }
                    }
                },
                osType = "Linux",
                restartPolicy = "Never",
                volumes = new[]
                {
                    new
                    {
                        name = "auto-archiver-storage",
                        azureFile = new
                        {
                            shareName = fileShareName,
                            storageAccountName = storageAccountName,
                            storageAccountKey = storageAccountKey
                        }
                    }
                },
                imageRegistryCredentials = !string.IsNullOrEmpty(registryServer) ? new object[]
                {
                    new
                    {
                        server = registryServer,
                        username = registryUsername,
                        password = registryPassword
                    }
                } : null,
                diagnostics = !string.IsNullOrEmpty(workspaceId) && !string.IsNullOrEmpty(workspaceKey) ? new
                {
                    logAnalytics = new
                    {
                        workspaceId = workspaceId,
                        workspaceKey = workspaceKey
                    }
                } : null
            }
        };
        
        // Get Azure management token
        var managementToken = await credential.GetTokenAsync(
            new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" }),
            default
        );
        
        // Create container using REST API
        var apiUrl = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.ContainerInstance/containerGroups/{containerGroupName}?api-version=2023-05-01";
        
        using var containerHttpClient = new HttpClient();
        containerHttpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", managementToken.Token);
        
        var requestJson = System.Text.Json.JsonSerializer.Serialize(containerRequest);
        Console.WriteLine($"[DEBUG] Creating container at: {apiUrl}");
        
        var containerResponse = await containerHttpClient.PutAsync(
            apiUrl,
            new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json")
        );
        
        if (!containerResponse.IsSuccessStatusCode)
        {
            var errorContent = await containerResponse.Content.ReadAsStringAsync();
            var errorMessage = $"Failed to create container via REST API (HTTP {containerResponse.StatusCode}): {errorContent}";
            Console.WriteLine($"[ERROR] {errorMessage}");
            throw new Exception(errorMessage);
        }
        
        Console.WriteLine($"[DEBUG] Container created successfully via REST API");
        
        if (!string.IsNullOrEmpty(workspaceId) && !string.IsNullOrEmpty(workspaceKey))
        {
            Console.WriteLine($"[DEBUG] Log Analytics configured: workspace {workspaceId}");
        }
        else
        {
            Console.WriteLine($"[WARNING] Log Analytics not configured - logs may not be available");
        }
        
        // Update job status to running after container is created
        if (activeJobs.TryGetValue(jobId, out var runningJob))
        {
            runningJob.Status = "running";
            runningJob.LogOutput += $"[ACI] Container group '{containerGroupName}' created successfully\n";
        }
        
        // Get container groups reference for status monitoring
        var containerGroups = resourceGroup.Value.GetContainerGroups();
        
        // Wait for completion (poll status)
        var maxWaitMinutes = configuration.GetValue<int>("AutoArchiver:ContainerMaxWaitMinutes", 180);
        var startTime = DateTime.UtcNow;
        string logs = "";
        
        while ((DateTime.UtcNow - startTime).TotalMinutes < maxWaitMinutes)
        {
            var currentGroup = await containerGroups.GetAsync(containerGroupName);
            var currentState = currentGroup.Value.Data.Containers[0].InstanceView?.CurrentState;
            
            if (currentState?.State == "Terminated")
            {
                // Wait for logs to propagate to Log Analytics (30-60 seconds typical)
                Console.WriteLine($"[DEBUG] Container terminated. Waiting 45 seconds for logs to propagate to Log Analytics...");
                await Task.Delay(45000);
                
                // Get logs from Log Analytics
                try
                {
                    var laWorkspaceId = configuration["AzureLogAnalytics:WorkspaceId"];
                    
                    if (!string.IsNullOrEmpty(laWorkspaceId))
                    {
                        Console.WriteLine($"[DEBUG] Querying Log Analytics for container logs...");
                        
                        // Query Log Analytics for container logs
                        var query = $@"
                            ContainerInstanceLog_CL
                            | where ContainerGroup_s == '{containerGroupName}'
                            | order by TimeGenerated asc
                            | top 5000 by TimeGenerated asc
                            | project TimeGenerated, Message";
                        
                        var logsUri = $"https://api.loganalytics.io/v1/workspaces/{laWorkspaceId}/query";
                        
                        using var httpClient = new HttpClient();
                        var requestBody = new { query = query };
                        var requestContent = new StringContent(
                            System.Text.Json.JsonSerializer.Serialize(requestBody),
                            System.Text.Encoding.UTF8,
                            "application/json"
                        );
                        
                        // Use Azure credential for authentication
                        var token = await credential.GetTokenAsync(
                            new Azure.Core.TokenRequestContext(new[] { "https://api.loganalytics.io/.default" }),
                            default
                        );
                        httpClient.DefaultRequestHeaders.Authorization = 
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
                        
                        var response = await httpClient.PostAsync(logsUri, requestContent);
                        
                        if (response.IsSuccessStatusCode)
                        {
                            var responseContent = await response.Content.ReadAsStringAsync();
                            var logData = System.Text.Json.JsonDocument.Parse(responseContent);
                            
                            // Extract log messages
                            var logMessages = new System.Text.StringBuilder();
                            if (logData.RootElement.TryGetProperty("tables", out var tables) && tables.GetArrayLength() > 0)
                            {
                                var firstTable = tables[0];
                                if (firstTable.TryGetProperty("rows", out var rows))
                                {
                                    foreach (var row in rows.EnumerateArray())
                                    {
                                        if (row.GetArrayLength() > 1)
                                        {
                                            var timestamp = row[0].GetString();
                                            var message = row[1].GetString();
                                            logMessages.AppendLine($"[{timestamp}] {message}");
                                        }
                                    }
                                }
                            }
                            
                            logs = logMessages.Length > 0 
                                ? logMessages.ToString() 
                                : "No logs found in Log Analytics. Container may have completed too quickly or logs are still propagating.";
                            
                            Console.WriteLine($"[DEBUG] Retrieved {logMessages.Length} characters from Log Analytics");
                        }
                        else
                        {
                            var errorContent = await response.Content.ReadAsStringAsync();
                            Console.WriteLine($"[ERROR] Log Analytics query failed: {response.StatusCode} - {errorContent}");
                            
                            // Provide helpful context for common errors
                            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                            {
                                logs = $"Log Analytics query failed: {response.StatusCode}\n" +
                                       $"Error details: {errorContent}\n\n" +
                                       "This is expected if containers haven't been configured to send logs to Log Analytics yet. " +
                                       "See configure-log-analytics.md for setup instructions.";
                            }
                            else
                            {
                                logs = $"Failed to query Log Analytics: {response.StatusCode}\n" +
                                       $"Error details: {errorContent}";
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[WARNING] Log Analytics not configured, cannot retrieve logs");
                        logs = "Log Analytics not configured. Container logs are not available.";
                    }
                }
                catch (Exception logEx)
                {
                    Console.WriteLine($"[ERROR] Failed to retrieve logs from Log Analytics: {logEx.Message}");
                    Console.WriteLine($"[ERROR] Log exception type: {logEx.GetType().Name}");
                    if (logEx.InnerException != null)
                    {
                        Console.WriteLine($"[ERROR] Inner exception: {logEx.InnerException.Message}");
                    }
                    logs = $"Could not retrieve container logs from Log Analytics: {logEx.Message}";
                }
                
                // Update job with completion status
                if (activeJobs.TryGetValue(jobId, out var completedJob))
                {
                    completedJob.Status = currentState.ExitCode == 0 ? "completed" : "failed";
                    completedJob.EndTime = DateTime.UtcNow;
                    completedJob.LogOutput += logs; // Append to preserve initialization messages
                    if (currentState.ExitCode != 0)
                    {
                        completedJob.ErrorMessage = $"Container exited with code {currentState.ExitCode}";
                    }
                }
                
                // Clean up container group to free quota
                try
                {
                    Console.WriteLine($"[DEBUG] Deleting container group: {containerGroupName}");
                    var deleteToken = await credential.GetTokenAsync(
                        new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" }),
                        default
                    );
                    
                    using var deleteHttpClient = new HttpClient();
                    deleteHttpClient.DefaultRequestHeaders.Authorization = 
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", deleteToken.Token);
                    
                    var deleteUrl = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.ContainerInstance/containerGroups/{containerGroupName}?api-version=2023-05-01";
                    var deleteResponse = await deleteHttpClient.DeleteAsync(deleteUrl);
                    
                    if (deleteResponse.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"[DEBUG] Successfully deleted container group: {containerGroupName}");
                    }
                    else
                    {
                        Console.WriteLine($"[WARNING] Failed to delete container group: {deleteResponse.StatusCode}");
                    }
                }
                catch (Exception deleteEx)
                {
                    Console.WriteLine($"[WARNING] Container cleanup failed: {deleteEx.Message}");
                    // Continue - we got the logs which is most important
                }
                
                return;
            }
            
            await Task.Delay(5000); // Poll every 5 seconds
        }
        
        // Timeout - try to get logs from Log Analytics anyway
        try
        {
            Console.WriteLine($"[DEBUG] Container timed out. Waiting for Log Analytics logs...");
            await Task.Delay(45000); // Wait for logs to propagate
            
            var laWorkspaceId = configuration["AzureLogAnalytics:WorkspaceId"];
            
            if (!string.IsNullOrEmpty(laWorkspaceId))
            {
                var query = $@"
                    ContainerInstanceLog_CL
                    | where ContainerGroup_s == '{containerGroupName}'
                    | order by TimeGenerated asc
                    | top 5000 by TimeGenerated asc
                    | project TimeGenerated, Message";
                
                var logsUri = $"https://api.loganalytics.io/v1/workspaces/{laWorkspaceId}/query";
                
                using var httpClient = new HttpClient();
                var requestBody = new { query = query };
                var requestContent = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(requestBody),
                    System.Text.Encoding.UTF8,
                    "application/json"
                );
                
                var token = await credential.GetTokenAsync(
                    new Azure.Core.TokenRequestContext(new[] { "https://api.loganalytics.io/.default" }),
                    default
                );
                httpClient.DefaultRequestHeaders.Authorization = 
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
                
                var response = await httpClient.PostAsync(logsUri, requestContent);
                
                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    var logData = System.Text.Json.JsonDocument.Parse(responseContent);
                    
                    var logMessages = new System.Text.StringBuilder();
                    if (logData.RootElement.TryGetProperty("tables", out var tables) && tables.GetArrayLength() > 0)
                    {
                        var firstTable = tables[0];
                        if (firstTable.TryGetProperty("rows", out var rows))
                        {
                            foreach (var row in rows.EnumerateArray())
                            {
                                if (row.GetArrayLength() > 1)
                                {
                                    var timestamp = row[0].GetString();
                                    var message = row[1].GetString();
                                    logMessages.AppendLine($"[{timestamp}] {message}");
                                }
                            }
                        }
                    }
                    logs = logMessages.Length > 0 
                        ? $"Container timed out after {maxWaitMinutes} minutes. Partial logs:\n{logMessages}" 
                        : $"Container timed out after {maxWaitMinutes} minutes and no logs found in Log Analytics.";    
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[ERROR] Log Analytics query failed: {response.StatusCode} - {errorContent}");
                    
                    // Provide helpful context for common errors
                    if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                    {
                        logs = $"Container timed out after {maxWaitMinutes} minutes. Log Analytics query failed: {response.StatusCode}\n" +
                               $"Error details: {errorContent}\n\n" +
                               "This is expected if containers haven't been configured to send logs to Log Analytics yet. " +
                               "See configure-log-analytics.md for setup instructions.";
                    }
                    else
                    {
                        logs = $"Container timed out after {maxWaitMinutes} minutes. Log Analytics query failed: {response.StatusCode}\n" +
                        $"Error details: {errorContent}";
                    }
                }
            }
            else
            {
                logs = $"Container timed out after {maxWaitMinutes} minutes and Log Analytics not configured.";
            }
        }
        catch (Exception logEx)
        {
            Console.WriteLine($"[ERROR] Failed to retrieve timeout logs: {logEx.Message}");
            logs = $"Container timed out after {maxWaitMinutes} minutes and logs could not be retrieved: {logEx.Message}";
        }
        
        // Update job with timeout status
        if (activeJobs.TryGetValue(jobId, out var timedOutJob))
        {
            timedOutJob.Status = "failed";
            timedOutJob.EndTime = DateTime.UtcNow;
            timedOutJob.LogOutput += logs; // Append to preserve initialization messages
            timedOutJob.ErrorMessage = $"Container execution exceeded the maximum wait time of {maxWaitMinutes} minutes (configured in AutoArchiver__ContainerMaxWaitMinutes). " +
                                       $"Either increase this app setting value or break your Google Sheet into smaller chunks with fewer URLs.";
        }
        
        // Clean up timed out container to free quota
        try
        {
            Console.WriteLine($"[DEBUG] Deleting timed out container group: {containerGroupName}");
            var deleteToken = await credential.GetTokenAsync(
                new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" }),
                default
            );
            
            using var deleteHttpClient = new HttpClient();
            deleteHttpClient.DefaultRequestHeaders.Authorization = 
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", deleteToken.Token);
            
            var deleteUrl = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.ContainerInstance/containerGroups/{containerGroupName}?api-version=2023-05-01";
            var deleteResponse = await deleteHttpClient.DeleteAsync(deleteUrl);
            
            if (deleteResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"[DEBUG] Successfully deleted timed out container: {containerGroupName}");
            }
            else
            {
                Console.WriteLine($"[WARNING] Failed to delete timed out container: {deleteResponse.StatusCode}");
            }
        }
        catch (Exception deleteEx)
        {
            Console.WriteLine($"[WARNING] Timed out container cleanup failed: {deleteEx.Message}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERROR] ACI Creation Failed: {ex.Message}");
        Console.WriteLine($"[ERROR] Stack Trace: {ex.StackTrace}");
        if (ex.InnerException != null)
        {
            Console.WriteLine($"[ERROR] Inner Exception: {ex.InnerException.Message}");
        }
        
        // Update job with error status
        if (activeJobs.TryGetValue(jobId, out var failedJob))
        {
            failedJob.Status = "failed";
            failedJob.EndTime = DateTime.UtcNow;
            failedJob.ErrorMessage = $"Error creating Azure Container Instance: {ex.Message}";
            failedJob.LogOutput += $"[ERROR] {ex.Message}\n{ex.StackTrace}\n";
        }
    }
}

// Helper function to setup ACI and create job entry (returns immediately)
async Task<(bool success, string jobId, string containerGroupName, string error)> SetupACIJob(
    string spreadsheetId,
    IConfiguration configuration)
{
    try
    {
        var jobId = Guid.NewGuid().ToString();
        var subscriptionId = configuration["AzureContainerInstance:SubscriptionId"];
        var resourceGroupName = configuration["AzureContainerInstance:ResourceGroupName"];
        
        // Validate configuration before creating job
        if (string.IsNullOrEmpty(subscriptionId) || string.IsNullOrEmpty(resourceGroupName))
        {
            return (false, "", "", "Azure Container Instance configuration is missing (SubscriptionId or ResourceGroupName)");
        }
        
        var storageAccountName = configuration["AzureStorage:AccountName"];
        var storageAccountKey = configuration["AzureStorage:AccountKey"];
        
        if (string.IsNullOrEmpty(storageAccountName) || string.IsNullOrEmpty(storageAccountKey))
        {
            return (false, "", "", "Azure Storage configuration is missing (AccountName or AccountKey)");
        }
        
        var containerGroupName = $"auto-archiver-{jobId}";
        
        // Create job entry with "starting" status
        var job = new AutoArchiverJob
        {
            JobId = jobId,
            JobType = "google-sheets-aci",
            Status = "starting",
            StartTime = DateTime.UtcNow,
            SpreadsheetId = spreadsheetId,
            Urls = new List<string>(),
            OutputDirectory = "azure-file-share",
            LogOutput = $"[ACI] Initializing Azure Container Instance '{containerGroupName}'...\n",
            ContainerGroupName = containerGroupName
        };
        
        lock (autoArchiverJobsLock)
        {
            activeJobs[jobId] = job;
        }
        
        return (true, jobId, containerGroupName, "");
    }
    catch (Exception ex)
    {
        return (false, "", "", $"Error setting up ACI job: {ex.Message}");
    }
}

// Helper function to run Auto-Archiver as background process for Google Sheets (shared between endpoints)
async Task<(bool success, string jobId, string error)> StartAutoArchiverProcessForSheets(string spreadsheetId, string pythonPath, string autoArchiverPath, IConfiguration configuration)
{
    try
    {
        var jobId = Guid.NewGuid().ToString();
        var installType = configuration["AutoArchiver:Install"] ?? "local";
        
        System.Diagnostics.Process process;
        string outputDirectory;
        
        if (installType.Equals("docker", StringComparison.OrdinalIgnoreCase))
        {
            // Docker installation
            outputDirectory = Path.Combine(Directory.GetCurrentDirectory(), "local_archive", jobId);
            Directory.CreateDirectory(outputDirectory);
            
            var configPath = Path.Combine(Directory.GetCurrentDirectory(), "secrets", "orchestration.yaml");
            if (!File.Exists(configPath))
            {
                return (false, "", $"Configuration file not found: {configPath}");
            }
            
            // Build Docker command
            var secretsPath = Path.Combine(Directory.GetCurrentDirectory(), "secrets");
            var localArchivePath = Path.Combine(Directory.GetCurrentDirectory(), "local_archive");
            
            process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "docker",
                    // Enable Docker-in-Docker for WACZ support (Browsertrix-Crawler)
                    // Mount Docker socket to allow nested Docker commands
                    Arguments = $"run --name auto-archiver-{jobId} -v \"/var/run/docker.sock:/var/run/docker.sock\" -v \"{secretsPath}:/app/secrets\" -v \"{localArchivePath}:/app/local_archive\" bellingcat/auto-archiver --feeders=gsheet_feeder_db --gsheet_feeder_db.sheet_id={spreadsheetId}",
                    WorkingDirectory = Directory.GetCurrentDirectory(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
        }
        else
        {
            // Local Python installation
            outputDirectory = autoArchiverPath; // Auto-Archiver handles output directory via config
            
            var configPath = configuration["AutoArchiver:LocalConfigPath"];
            if (string.IsNullOrEmpty(configPath))
            {
                return (false, "", "AutoArchiver:LocalConfigPath configuration is missing");
            }
            
            if (!File.Exists(configPath))
            {
                return (false, "", $"Configuration file not found: {configPath}");
            }
            
            process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = pythonPath,
                    Arguments = $"-m auto_archiver --config \"{configPath}\" --gsheet_feeder_db.sheet_id={spreadsheetId}",
                    WorkingDirectory = autoArchiverPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
        }
        
        // Track the job
        var job = new AutoArchiverJob
        {
            JobId = jobId,
            JobType = "google-sheets",
            Status = "starting",
            StartTime = DateTime.UtcNow,
            SpreadsheetId = spreadsheetId,
            Urls = new List<string>(), // Empty for sheets jobs
            OutputDirectory = outputDirectory,
            LogOutput = ""
        };
        
        process.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                job.LogOutput += $"[OUT] {DateTime.UtcNow:HH:mm:ss} {e.Data}\n";
            }
        };
        
        process.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                job.LogOutput += $"[ERR] {DateTime.UtcNow:HH:mm:ss} {e.Data}\n";
            }
        };
        
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        
        job.ProcessId = process.Id;
        job.Status = "running";
        activeJobs[jobId] = job;
        
        // Monitor process completion in background
        _ = Task.Run(async () =>
        {
            await process.WaitForExitAsync();
            
            if (activeJobs.TryGetValue(jobId, out var completedJob))
            {
                completedJob.Status = process.ExitCode == 0 ? "completed" : "failed";
                completedJob.EndTime = DateTime.UtcNow;
                completedJob.LogOutput += $"\n[SYS] Process completed with exit code {process.ExitCode}";
            }
        });
        
        return (true, jobId, "");
    }
    catch (Exception ex)
    {
        return (false, "", $"Error starting Auto-Archiver for Google Sheets: {ex.Message}");
    }
}

// Auto-Archiver Google Sheets asynchronous endpoint - starts archiving process in background
app.MapPost("/bellingcat/auto-archiver-sheets-asynchronous", async (HttpContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration) =>
{
    try
    {
        var url = context.Request.Query["url"];
        
        if (string.IsNullOrEmpty(url))
        {
            return Results.BadRequest(new { message = "URL parameter is required" });
        }

        // Extract spreadsheet ID and GID from Google Sheets URL
        var (spreadsheetId, gid) = ExtractSpreadsheetInfo(url!);
        
        if (string.IsNullOrEmpty(spreadsheetId))
        {
            return Results.BadRequest(new { 
                message = "Invalid Google Sheets URL. Expected format: https://docs.google.com/spreadsheets/d/{SPREADSHEET_ID}/edit#gid={SHEET_ID}",
                providedUrl = url.ToString()
            });
        }

        // Setup Google Sheets client and get sheet metadata
        var (setupSuccess, httpClient, sheetName, setupError) = await SetupGoogleSheetsClient(httpClientFactory, configuration, spreadsheetId, gid);
        if (!setupSuccess)
        {
            return Results.Problem(setupError, statusCode: 500);
        }

        // Prepare sheet columns for Auto-Archiver
        var (prepareSuccess, prepareError) = await PrepareAutoArchiverSheetColumns(httpClient!, spreadsheetId, sheetName!, gid, configuration);
        if (!prepareSuccess)
        {
            return Results.Problem(prepareError, statusCode: 500);
        }

        // Setup Auto-Archiver environment
        var installType = configuration["AutoArchiver:Install"] ?? "local";
        string pythonPath = "";
        string autoArchiverPath = "";
        string jobId = "";
        
        if (installType.Equals("aci", StringComparison.OrdinalIgnoreCase))
        {
            // Azure Container Instances mode - skip environment setup
            // ACI will be created directly by StartAutoArchiverWithACI
        }
        else if (installType.Equals("docker", StringComparison.OrdinalIgnoreCase))
        {
            // For Docker, just verify it's installed
            var (envSuccess, _, _, envError) = await SetupAutoArchiverEnvironmentForSheets();
            if (!envSuccess)
            {
                return Results.Problem(new { 
                    message = "Auto-Archiver environment setup failed",
                    error = envError,
                    instructions = new {
                        step1 = "Install Docker and ensure it's running",
                        step2 = "Pull Auto-Archiver image: docker pull bellingcat/auto-archiver:latest"
                    }
                }.ToString() ?? "", statusCode: 500);
            }
            pythonPath = "docker";
            autoArchiverPath = Directory.GetCurrentDirectory();
        }
        else
        {
            // For local, setup Python environment
            var (envSuccess, pyPath, aaPath, envError) = await SetupAutoArchiverEnvironmentForSheets();
            if (!envSuccess)
            {
                return Results.Problem(new { 
                    message = "Auto-Archiver environment setup failed",
                    error = envError,
                    instructions = new {
                        step1 = "Install Python 3.8+ and ensure it's in PATH",
                        step2 = "Install Auto-Archiver: pip install auto-archiver",
                        step3 = "Ensure orchestration_sheets.yaml exists in auto-archiver directory"
                    }
                }.ToString() ?? "", statusCode: 500);
            }
            pythonPath = pyPath;
            autoArchiverPath = aaPath;
        }

        // Count URLs in sheet and calculate time estimates
        var (dataSuccess, values, urlColumnIndex, _, _, _) = await PrepareSheetData(httpClient!, spreadsheetId, sheetName!, gid, configuration);
        int urlCount = 0;
        int videoUrlCount = 0;
        int regularUrlCount = 0;
        
        if (dataSuccess && values != null && urlColumnIndex >= 0)
        {
            // Skip header row and count URLs by type
            for (int i = 1; i < values.Count; i++)
            {
                var row = values[i];
                if (row.GetArrayLength() > urlColumnIndex)
                {
                    var urlValue = row[urlColumnIndex].GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(urlValue))
                    {
                        urlCount++;
                        // Check if URL contains video-related keywords
                        var urlLower = urlValue.ToLower();
                        if (urlLower.Contains("video") || urlLower.Contains("watch"))
                        {
                            videoUrlCount++;
                        }
                        else
                        {
                            regularUrlCount++;
                        }
                    }
                }
            }
        }
        
        // Calculate time estimate: video URLs = 2-5 min, regular URLs = 15-30 sec
        int minTimeSeconds = (videoUrlCount * 120) + (regularUrlCount * 15); // video: 2 min, regular: 15 sec
        int maxTimeSeconds = (videoUrlCount * 300) + (regularUrlCount * 30); // video: 5 min, regular: 30 sec
        
        // Add 3 minutes for ACI container image pull time
        if (installType.Equals("aci", StringComparison.OrdinalIgnoreCase))
        {
            minTimeSeconds += 180; // 3 minutes = 180 seconds
            maxTimeSeconds += 180;
        }
        
        string estimatedTime = urlCount > 0 
            ? $"{Math.Round(minTimeSeconds / 60.0, 1)}-{Math.Round(maxTimeSeconds / 60.0, 1)} minutes"
            : "Unknown";

        // Start Auto-Archiver process based on installation mode
        if (installType.Equals("aci", StringComparison.OrdinalIgnoreCase))
        {
            // Check if estimated time exceeds container max wait time
            var containerMaxWaitMinutes = configuration.GetValue<int>("AutoArchiver:ContainerMaxWaitMinutes", 180);
            var containerMaxWaitSeconds = containerMaxWaitMinutes * 60;
            
            if (maxTimeSeconds > containerMaxWaitSeconds)
            {
                return Results.Problem(
                    $"Estimated processing time ({Math.Round(maxTimeSeconds / 60.0, 1)} minutes) exceeds the container maximum wait time ({containerMaxWaitMinutes} minutes). " +
                    $"Please either increase the 'AutoArchiver__ContainerMaxWaitMinutes' app setting in Azure, or break your Google Sheet into smaller chunks. " +
                    $"Current sheet has {urlCount} URLs ({videoUrlCount} videos, {regularUrlCount} regular URLs).",
                    statusCode: 413 // Payload Too Large
                );
            }
                
            // Azure Container Instances - setup job and return immediately, then monitor in background
            var (aciSetupSuccess, aciJobId, containerGroupName, aciSetupError) = await SetupACIJob(spreadsheetId, configuration);
            
            if (!aciSetupSuccess)
            {
                return Results.Problem(aciSetupError, statusCode: 500);
            }
            
            jobId = aciJobId;
            
            // Start background task to create and monitor ACI
            _ = Task.Run(async () => await CreateAndMonitorACI(jobId, spreadsheetId, configuration));
        }
        else
        {
            // Docker or Local Python - run asynchronously
            var (startSuccess, startJobId, startError) = await StartAutoArchiverProcessForSheets(spreadsheetId, pythonPath, autoArchiverPath, configuration);
            if (!startSuccess)
            {
                return Results.Problem(startError, statusCode: 500);
            }
            jobId = startJobId;
        }

        return Results.Ok(new {
            message = "Auto-Archiver Google Sheets job started successfully",
            jobId = jobId,
            jobType = "google-sheets",
            status = "starting",
            installMode = installType,
            spreadsheetId = spreadsheetId,
            sheetName = sheetName,
            estimatedUrlCount = urlCount,
            videoUrlCount = videoUrlCount,
            regularUrlCount = regularUrlCount,
            estimatedTime = estimatedTime,
            checkStatusUrl = $"/bellingcat/auto-archiver/status/{jobId}",
            note = installType.Equals("aci", StringComparison.OrdinalIgnoreCase)
                ? "Azure Container Instance is being created. This may take 2-3 minutes for image pull. Use Check Status to monitor progress."
                : "Use the status endpoint to monitor progress. Auto-Archiver will write results directly to the Google Sheet."
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error starting Auto-Archiver sheets job: {ex.Message}", statusCode: 500);
    }

    // Helper function to prepare Google Sheet columns for Auto-Archiver
    async Task<(bool success, string? error)> PrepareAutoArchiverSheetColumns(
        HttpClient httpClient, string spreadsheetId, string sheetName, int? gid, IConfiguration configuration)
    {
        try
        {
            // Get the sheet data
            var apiUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}/values/{Uri.EscapeDataString(sheetName)}";
            var response = await httpClient.GetAsync(apiUrl);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                return (false, $"Google Sheets API error: {errorContent}");
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            var sheetsData = JsonDocument.Parse(responseContent);
            
            if (!sheetsData.RootElement.TryGetProperty("values", out var valuesElement))
            {
                return (false, "No data found in the sheet");
            }

            var values = valuesElement.EnumerateArray().ToList();
            if (values.Count == 0)
            {
                return (false, "No data found in the sheet");
            }

            // First row as headers
            var headers = values[0].EnumerateArray().Select(cell => cell.GetString() ?? "").ToList();
            
            // Find required column indices
            var urlColumnIndex = -1;
            var archiveStatusColumnIndex = -1;
            var archiveUrlColumnIndex = -1;
            
            for (int i = 0; i < headers.Count; i++)
            {
                var header = headers[i].ToLower();
                if (header == "url")
                {
                    urlColumnIndex = i;
                }
                else if (header.Contains("archive") && header.Contains("status"))
                {
                    archiveStatusColumnIndex = i;
                }
                else if (header.Contains("archive") && header.Contains("url"))
                {
                    archiveUrlColumnIndex = i;
                }
            }

            if (urlColumnIndex == -1)
            {
                return (false, "No 'URL' column found in the sheet. Please add a column with header 'URL'");
            }

            // Add missing columns
            var columnsToAdd = new List<(int index, string name)>();
            
            if (archiveStatusColumnIndex == -1)
            {
                archiveStatusColumnIndex = headers.Count;
                columnsToAdd.Add((archiveStatusColumnIndex, "Archive Status"));
            }
            
            if (archiveUrlColumnIndex == -1)
            {
                archiveUrlColumnIndex = archiveStatusColumnIndex != -1 ? archiveStatusColumnIndex + 1 : headers.Count;
                columnsToAdd.Add((archiveUrlColumnIndex, "Archive URL"));
            }

            // Add additional Auto-Archiver result columns after Archive URL if they don't exist
            var additionalColumns = new[] { 
                "Archive Date", "Upload Timestamp", "Upload Title", "Text Content", 
                "Screenshot", "Hash", "WACZ", "ReplayWebpage" 
            };
            
            var nextColumnIndex = Math.Max(archiveUrlColumnIndex + 1, headers.Count);
            
            foreach (var columnName in additionalColumns)
            {
                // Check if column already exists
                var existingIndex = headers.FindIndex(h => h.Equals(columnName, StringComparison.OrdinalIgnoreCase));
                if (existingIndex == -1)
                {
                    columnsToAdd.Add((nextColumnIndex, columnName));
                    nextColumnIndex++;
                }
            }

            // Add missing columns to sheet
            foreach (var (index, name) in columnsToAdd)
            {
                var columnLetter = GetColumnLetter(index);
                var addHeaderRequest = new
                {
                    range = $"{sheetName}!{columnLetter}1",
                    values = new[] { new[] { name } }
                };

                var addHeaderUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}/values/{Uri.EscapeDataString($"{sheetName}!{columnLetter}1")}?valueInputOption=RAW";
                var addHeaderResponse = await httpClient.PutAsync(
                    addHeaderUrl,
                    new StringContent(JsonSerializer.Serialize(addHeaderRequest), Encoding.UTF8, "application/json")
                );

                if (!addHeaderResponse.IsSuccessStatusCode)
                {
                    var headerError = await addHeaderResponse.Content.ReadAsStringAsync();
                    return (false, $"Failed to add '{name}' column: {headerError}");
                }
                
                Console.WriteLine($"Successfully added '{name}' column at index {index} (column {columnLetter})");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Exception preparing sheet columns: {ex.Message}");
        }
    }

    // Helper function to setup Auto-Archiver environment
    async Task<(bool success, string pythonPath, string autoArchiverPath, string error)> SetupAutoArchiverEnvironmentForSheets()
    {
        try
        {
            var installType = configuration["AutoArchiver:Install"] ?? "local";
            
            // For Docker installation, just verify Docker is available
            if (installType.Equals("docker", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var dockerTest = new System.Diagnostics.Process
                    {
                        StartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "docker",
                            Arguments = "--version",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        }
                    };
                    
                    dockerTest.Start();
                    await dockerTest.WaitForExitAsync();
                    
                    if (dockerTest.ExitCode == 0)
                    {
                        return (true, "docker", Directory.GetCurrentDirectory(), "");
                    }
                }
                catch { }
                
                return (false, "", "", "Docker not found. Please install Docker and ensure it's running.");
            }
            
            // For local installation, check if Python is available
            var pythonPaths = new[] { "python", "python3", "py" };
            string workingPythonPath = "";
            
            foreach (var pythonCmd in pythonPaths)
            {
                try
                {
                    var testProcess = new System.Diagnostics.Process
                    {
                        StartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = pythonCmd,
                            Arguments = "--version",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        }
                    };
                    
                    testProcess.Start();
                    await testProcess.WaitForExitAsync();
                    
                    if (testProcess.ExitCode == 0)
                    {
                        workingPythonPath = pythonCmd;
                        break;
                    }
                }
                catch { continue; }
            }
            
            if (string.IsNullOrEmpty(workingPythonPath))
            {
                return (false, "", "", "Python not found. Please install Python 3.8+ and ensure it's in PATH");
            }
            
            // Check for Auto-Archiver installation path
            var clonedPath = configuration["AutoArchiver:LocalClonedRepoPath"];
            if (!string.IsNullOrEmpty(clonedPath) && Directory.Exists(clonedPath))
            {
                return (true, workingPythonPath, clonedPath, "");
            }
            
            // Auto-Archiver not found
            return (false, workingPythonPath, "", 
                "Auto-Archiver directory not found. Please configure AutoArchiver:LocalClonedRepoPath in appsettings.json");
        }
        catch (Exception ex)
        {
            return (false, "", "", $"Error setting up Auto-Archiver environment: {ex.Message}");
        }
    }
});

// Auto-Archiver status endpoint - checks job progress
app.MapGet("/bellingcat/auto-archiver/status/{jobId}", (string jobId) =>
{
    if (!activeJobs.TryGetValue(jobId, out var job))
    {
        return Results.NotFound(new { message = "Job not found", jobId });
    }

    var response = new {
        jobId = job.JobId,
        jobType = job.JobType,
        status = job.Status,
        startTime = job.StartTime,
        endTime = job.EndTime,
        duration = job.EndTime.HasValue 
            ? job.EndTime.Value - job.StartTime 
            : DateTime.UtcNow - job.StartTime,
        spreadsheetId = job.JobType == "google-sheets" || job.JobType == "google-sheets-aci" ? job.SpreadsheetId : null,
        urls = job.JobType == "url-list" ? job.Urls : null,
        urlCount = job.Urls.Count,
        outputDirectory = job.OutputDirectory,
        containerGroupName = !string.IsNullOrEmpty(job.ContainerGroupName) ? job.ContainerGroupName : null,
        logOutput = job.LogOutput,
        errorMessage = !string.IsNullOrEmpty(job.ErrorMessage) ? job.ErrorMessage : null,
        results = job.Status == "completed" && job.JobType != "google-sheets-aci" ? GetJobResults(job.OutputDirectory) : null,
        note = job.JobType == "google-sheets-aci" && job.Status == "completed" ? "Results written to Google Sheet." : null
    };

    return Results.Ok(response);
});

// Helper function to collect job results
object? GetJobResults(string outputDirectory)
{
    try
    {
        if (!Directory.Exists(outputDirectory))
        {
            return new { message = "Output directory not found" };
        }

        var files = Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories)
            .Select(f => new {
                name = Path.GetFileName(f),
                path = f.Replace(outputDirectory, "").TrimStart(Path.DirectorySeparatorChar),
                size = new FileInfo(f).Length,
                created = File.GetCreationTime(f)
            }).ToList();

        var resultSummary = new {
            totalFiles = files.Count,
            screenshots = files.Count(f => f.name.EndsWith(".png") || f.name.EndsWith(".jpg")),
            archives = files.Count(f => f.name.EndsWith(".warc") || f.name.EndsWith(".zip")),
            metadata = files.Count(f => f.name.EndsWith(".json") || f.name.EndsWith(".yaml")),
            files = files.Take(20).ToList(), // Limit to first 20 files
            outputPath = outputDirectory
        };

        return resultSummary;
    }
    catch (Exception ex)
    {
        return new { error = $"Error reading results: {ex.Message}" };
    }
}

app.Run();

// Data structure for URL processing results
public class UrlProcessingJob
{
    public int RowIndex { get; set; }
    public string UrlValue { get; set; } = "";
    public string CellValue { get; set; } = "";
    public bool IsError { get; set; }
}

// Data structure for Auto-Archiver job tracking
public class AutoArchiverJob
{
    public string JobId { get; set; } = "";
    public string JobType { get; set; } = ""; // "url-list" or "google-sheets"
    public string Status { get; set; } = "";
    public List<string> Urls { get; set; } = new List<string>();
    public string SpreadsheetId { get; set; } = ""; // For Google Sheets jobs
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string OutputPath { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public List<string> Logs { get; set; } = new List<string>();
    public string LogOutput { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public int ProcessId { get; set; }
    public string ContainerGroupName { get; set; } = ""; // For Azure Container Instances jobs
} async (HttpContext context, IHttpClientFactory httpClientFactory, IConfiguration configuration) =>
{
    try
    {
        var url = context.Request.Query["url"];
        
        if (string.IsNullOrEmpty(url))
        {
            return Results.BadRequest(new { message = "URL parameter is required" });
        }

        // Extract spreadsheet ID and GID from Google Sheets URL
        var (spreadsheetId, gid) = ExtractSpreadsheetInfo(url!);
        
        if (string.IsNullOrEmpty(spreadsheetId))
        {
            return Results.BadRequest(new { 
                message = "Invalid Google Sheets URL. Expected format: https://docs.google.com/spreadsheets/d/{SPREADSHEET_ID}/edit#gid={SHEET_ID}",
                providedUrl = url.ToString()
            });
        }

        // Setup Google Sheets client and get sheet metadata
        var (setupSuccess, httpClient, sheetName, setupError) = await SetupGoogleSheetsClient(httpClientFactory, configuration, spreadsheetId, gid);
        if (!setupSuccess)
        {
            return Results.Problem(setupError, statusCode: 500);
        }

        // Prepare sheet columns for Auto-Archiver
        var (prepareSuccess, prepareError) = await PrepareAutoArchiverSheetColumns(httpClient!, spreadsheetId, sheetName!, gid, configuration);
        if (!prepareSuccess)
        {
            return Results.Problem(prepareError, statusCode: 500);
        }

        // Setup Auto-Archiver environment
        var (envSuccess, pythonPath, autoArchiverPath, envError) = await SetupAutoArchiverEnvironmentForSheets();
        if (!envSuccess)
        {
            var installType = configuration["AutoArchiver:Install"] ?? "local";
            
            if (installType.Equals("docker", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Problem(new { 
                    message = "Docker environment setup failed",
                    error = envError,
                    instructions = new {
                        step1 = "Install Docker Desktop",
                        step2 = "Ensure Docker is running",
                        step3 = "Verify Docker is accessible from command line: docker --version"
                    }
                }.ToString() ?? "", statusCode: 500);
            }
            else
            {
                return Results.Problem(new { 
                    message = "Auto-Archiver environment setup failed",
                    error = envError,
                    instructions = new {
                        step1 = "Install Python 3.8+ and ensure it's in PATH",
                        step2 = "Install Auto-Archiver: pip install auto-archiver",
                        step3 = "Ensure orchestration_sheets.yaml exists in auto-archiver directory"
                    }
                }.ToString() ?? "", statusCode: 500);
            }
        }

        // Start Auto-Archiver process with Google Sheets feeder
        var (archiveSuccess, processedCount, archivedCount, failedCount, archiveError) = 
            await RunAutoArchiverWithSheets(spreadsheetId, pythonPath, autoArchiverPath);
        
        if (!archiveSuccess)
        {
            return Results.Problem(archiveError, statusCode: 500);
        }

        // Count total records from sheet
        var (dataSuccess, values, _, _, _, _) = await PrepareSheetData(httpClient!, spreadsheetId, sheetName!, gid, configuration);
        int totalRecords = dataSuccess && values != null ? values.Count - 1 : 0;

        return Results.Ok(new { 
            message = processedCount > 0 
                ? $"Successfully processed {processedCount} URLs and archived {archivedCount} of them"
                : "No URLs found to archive",
            totalRecords = totalRecords,
            processedCount = processedCount,
            archivedCount = archivedCount,
            failedCount = failedCount,
            skippedCount = totalRecords - processedCount,
            successRate = processedCount > 0 ? Math.Round((double)archivedCount / processedCount * 100, 1) : 0,
            spreadsheetId = spreadsheetId,
            sheetName = sheetName,
            sourceUrl = url.ToString(),
            columnsUpdated = new[] { "Archive Status", "Archive URL" },
            note = "Archive results are written to the Google Sheet by Auto-Archiver."
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error processing Auto-Archiver sheets request: {ex.Message}", statusCode: 500);
    }

    // Helper function to prepare Google Sheet columns for Auto-Archiver
    async Task<(bool success, string? error)> PrepareAutoArchiverSheetColumns(
        HttpClient httpClient, string spreadsheetId, string sheetName, int? gid, IConfiguration configuration)
    {
        try
        {
            // Get the sheet data
            var apiUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}/values/{Uri.EscapeDataString(sheetName)}";
            var response = await httpClient.GetAsync(apiUrl);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                return (false, $"Google Sheets API error: {errorContent}");
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            var sheetsData = JsonDocument.Parse(responseContent);
            
            if (!sheetsData.RootElement.TryGetProperty("values", out var valuesElement))
            {
                return (false, "No data found in the sheet");
            }

            var values = valuesElement.EnumerateArray().ToList();
            if (values.Count == 0)
            {
                return (false, "No data found in the sheet");
            }

            // First row as headers
            var headers = values[0].EnumerateArray().Select(cell => cell.GetString() ?? "").ToList();
            
            // Find required column indices
            var urlColumnIndex = -1;
            var archiveStatusColumnIndex = -1;
            var archiveUrlColumnIndex = -1;
            
            for (int i = 0; i < headers.Count; i++)
            {
                var header = headers[i].ToLower();
                if (header == "url")
                {
                    urlColumnIndex = i;
                }
                else if (header.Contains("archive") && header.Contains("status"))
                {
                    archiveStatusColumnIndex = i;
                }
                else if (header.Contains("archive") && header.Contains("url"))
                {
                    archiveUrlColumnIndex = i;
                }
            }

            if (urlColumnIndex == -1)
            {
                return (false, "No 'URL' column found in the sheet. Please add a column with header 'URL'");
            }

            // Add missing columns
            var columnsToAdd = new List<(int index, string name)>();
            
            if (archiveStatusColumnIndex == -1)
            {
                archiveStatusColumnIndex = headers.Count;
                columnsToAdd.Add((archiveStatusColumnIndex, "Archive Status"));
            }
            
            if (archiveUrlColumnIndex == -1)
            {
                archiveUrlColumnIndex = archiveStatusColumnIndex != -1 ? archiveStatusColumnIndex + 1 : headers.Count;
                columnsToAdd.Add((archiveUrlColumnIndex, "Archive URL"));
            }
            
            // Add additional Auto-Archiver result columns after Archive URL if they don't exist
            var additionalColumns = new[] { 
                "Archive Date", "Upload Timestamp", "Upload Title", "Text Content", 
                "Screenshot", "Hash", "WACZ", "ReplayWebpage" 
            };
            
            var nextColumnIndex = Math.Max(archiveUrlColumnIndex + 1, headers.Count);
            
            foreach (var columnName in additionalColumns)
            {
                // Check if column already exists
                var existingIndex = headers.FindIndex(h => h.Equals(columnName, StringComparison.OrdinalIgnoreCase));
                if (existingIndex == -1)
                {
                    columnsToAdd.Add((nextColumnIndex, columnName));
                    nextColumnIndex++;
                }
            }

            // Add missing columns to sheet
            foreach (var (index, name) in columnsToAdd)
            {
                var columnLetter = GetColumnLetter(index);
                var addHeaderRequest = new
                {
                    range = $"{sheetName}!{columnLetter}1",
                    values = new[] { new[] { name } }
                };

                var addHeaderUrl = $"https://sheets.googleapis.com/v4/spreadsheets/{spreadsheetId}/values/{Uri.EscapeDataString($"{sheetName}!{columnLetter}1")}?valueInputOption=RAW";
                var addHeaderResponse = await httpClient.PutAsync(
                    addHeaderUrl,
                    new StringContent(JsonSerializer.Serialize(addHeaderRequest), Encoding.UTF8, "application/json")
                );

                if (!addHeaderResponse.IsSuccessStatusCode)
                {
                    var headerError = await addHeaderResponse.Content.ReadAsStringAsync();
                    return (false, $"Failed to add '{name}' column: {headerError}");
                }
                
                Console.WriteLine($"Successfully added '{name}' column at index {index} (column {columnLetter})");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Exception preparing sheet columns: {ex.Message}");
        }
    }

    // Helper function to setup Auto-Archiver environment
    async Task<(bool success, string pythonPath, string autoArchiverPath, string error)> SetupAutoArchiverEnvironmentForSheets()
    {
        try
        {
            var installType = configuration["AutoArchiver:Install"] ?? "local";
            
            // For Docker installation, just verify Docker is available
            if (installType.Equals("docker", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var dockerTest = new System.Diagnostics.Process
                    {
                        StartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "docker",
                            Arguments = "--version",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        }
                    };
                    
                    dockerTest.Start();
                    await dockerTest.WaitForExitAsync();
                    
                    if (dockerTest.ExitCode == 0)
                    {
                        return (true, "docker", Directory.GetCurrentDirectory(), "");
                    }
                }
                catch { }
                
                return (false, "", "", "Docker not found. Please install Docker and ensure it's running.");
            }
            
            // For local installation, check if Python is available
            var pythonPaths = new[] { "python", "python3", "py" };
            string workingPythonPath = "";
            
            foreach (var pythonCmd in pythonPaths)
            {
                try
                {
                    var testProcess = new System.Diagnostics.Process
                    {
                        StartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = pythonCmd,
                            Arguments = "--version",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        }
                    };
                    
                    testProcess.Start();
                    await testProcess.WaitForExitAsync();
                    
                    if (testProcess.ExitCode == 0)
                    {
                        workingPythonPath = pythonCmd;
                        break;
                    }
                }
                catch { continue; }
            }
            
            if (string.IsNullOrEmpty(workingPythonPath))
            {
                return (false, "", "", "Python not found. Please install Python 3.8+ and ensure it's in PATH");
            }
            
            // Check for Auto-Archiver installation path
            var clonedPath = configuration["AutoArchiver:LocalClonedRepoPath"];
            if (!string.IsNullOrEmpty(clonedPath) && Directory.Exists(clonedPath))
            {
                return (true, workingPythonPath, clonedPath, "");
            }
            
            // Auto-Archiver not found
            return (false, workingPythonPath, "", 
                "Auto-Archiver directory not found. Please configure AutoArchiver:LocalClonedRepoPath in appsettings.json");
        }
        catch (Exception ex)
        {
            return (false, "", "", $"Error setting up Auto-Archiver environment: {ex.Message}");
        }
    }

    // Helper function to run Auto-Archiver with Google Sheets feeder
    async Task<(bool success, int processedCount, int archivedCount, int failedCount, string error)> 
        RunAutoArchiverWithSheets(string spreadsheetId, string pythonPath, string autoArchiverPath)
    {
        try
        {
            var jobId = Guid.NewGuid().ToString();
            
            // Use the orchestration_sheets.yaml config file
            var configPath = configuration["AutoArchiver:LocalConfigPath"];
            if (string.IsNullOrEmpty(configPath))
            {
                return (false, 0, 0, 0, "AutoArchiver:LocalConfigPath configuration is missing");
            }
            
            if (!File.Exists(configPath))
            {
                return (false, 0, 0, 0, $"Configuration file not found: {configPath}");
            }

            // Start Auto-Archiver process with Google Sheets feeder
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = pythonPath,
                    Arguments = $"-m auto_archiver --config \"{configPath}\" --gsheet_feeder_db.sheet_id={spreadsheetId}",
                    WorkingDirectory = autoArchiverPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            
            var logOutput = new System.Text.StringBuilder();
            
            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    logOutput.AppendLine($"[OUT] {e.Data}");
                    Console.WriteLine($"[AUTO-ARCHIVER OUT] {e.Data}");
                }
            };
            
            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    logOutput.AppendLine($"[ERR] {e.Data}");
                    Console.WriteLine($"[AUTO-ARCHIVER ERR] {e.Data}");
                }
            };
            
            Console.WriteLine($"Starting Auto-Archiver with spreadsheet ID: {spreadsheetId}");
            Console.WriteLine($"Command: {process.StartInfo.FileName} {process.StartInfo.Arguments}");
            
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            
            // Wait for process to complete
            await process.WaitForExitAsync();
            
            Console.WriteLine($"Auto-Archiver process completed with exit code: {process.ExitCode}");
            
            if (process.ExitCode != 0)
            {
                return (false, 0, 0, 0, $"Auto-Archiver process failed with exit code {process.ExitCode}. Log: {logOutput}");
            }

            // Parse results from Auto-Archiver root directory
            var (processedCount, archivedCount, failedCount) = ParseAutoArchiverResults(autoArchiverPath);
            
            return (true, processedCount, archivedCount, failedCount, "");
        }
        catch (Exception ex)
        {
            return (false, 0, 0, 0, $"Error running Auto-Archiver: {ex.Message}");
        }
    }

    // Helper function to parse Auto-Archiver results
    (int processedCount, int archivedCount, int failedCount) ParseAutoArchiverResults(string autoArchiverPath)
    {
        try
        {
            // Look for db.csv file in Auto-Archiver root directory
            var csvPath = Path.Combine(autoArchiverPath, "db.csv");
            
            if (!File.Exists(csvPath))
            {
                Console.WriteLine($"No db.csv file found in {autoArchiverPath}");
                return (0, 0, 0);
            }

            var lines = File.ReadAllLines(csvPath);
            
            if (lines.Length <= 1)
            {
                // Only header or empty file
                return (0, 0, 0);
            }

            int processedCount = 0;
            int archivedCount = 0;
            int failedCount = 0;

            // Parse CSV to count successful vs failed archives (skip empty lines)
            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                
                // Skip empty lines
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                
                processedCount++; // Count non-empty data rows
                
                // Check if status contains "success" or "archived" (case-insensitive)
                if (line.ToLower().Contains("success") || 
                    line.ToLower().Contains("archived") ||
                    (!line.ToLower().Contains("nothing archived") && !line.ToLower().Contains("failed")))
                {
                    archivedCount++;
                }
                else
                {
                    failedCount++;
                }
            }

            Console.WriteLine($"Parsed results: {processedCount} processed, {archivedCount} archived, {failedCount} failed");
            
            return (processedCount, archivedCount, failedCount);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing Auto-Archiver results: {ex.Message}");
            return (0, 0, 0);
        }
    }
});

// Static file serving endpoint for WACZ archives
app.MapGet("/archives/{filename}", async (string filename, HttpContext context) =>
{
    try
    {
        // Security: Only allow .wacz files
        if (!filename.EndsWith(".wacz", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { message = "Only .wacz files are allowed" });
        }

        // Construct the file path
        var archivePath = Path.Combine(Directory.GetCurrentDirectory(), "local_archive", filename);
        
        if (!File.Exists(archivePath))
        {
            return Results.NotFound(new { message = $"Archive file not found: {filename}" });
        }

        // Set CORS headers to allow ReplayWeb.page to access the file
        context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
        context.Response.Headers.Append("Access-Control-Allow-Methods", "GET, OPTIONS");
        context.Response.Headers.Append("Access-Control-Allow-Headers", "Range, Content-Type");
        context.Response.Headers.Append("Access-Control-Expose-Headers", "Content-Length, Content-Range, Accept-Ranges");
        
        // Return the file with proper content type
        return Results.File(archivePath, "application/wacz+zip", filename, enableRangeProcessing: true);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error serving archive file: {ex.Message}", statusCode: 500);
    }
});

app.Run();

// Data structure for URL processing results
public class UrlProcessingJob
{
    public int RowIndex { get; set; }
    public string UrlValue { get; set; } = "";
    public string CellValue { get; set; } = "";
    public bool IsError { get; set; }
}

// Data structure for Auto-Archiver job tracking
public class AutoArchiverJob
{
    public string JobId { get; set; } = "";
    public string JobType { get; set; } = ""; // "url-list" or "google-sheets"
    public string Status { get; set; } = "";
    public List<string> Urls { get; set; } = new List<string>();
    public string SpreadsheetId { get; set; } = ""; // For Google Sheets jobs
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string OutputPath { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public List<string> Logs { get; set; } = new List<string>();
    public string LogOutput { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public int ProcessId { get; set; }
    public string ContainerGroupName { get; set; } = ""; // For Azure Container Instances jobs
}