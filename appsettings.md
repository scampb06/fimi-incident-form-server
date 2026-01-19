# Azure App Settings Configuration Guide

This document describes all configuration parameters required for the FIMI Incident Form Server. These settings should be configured using the PowerShell script `configure-azure-app-settings.ps1`.

## Azure Resource Configuration

### `<AZURE_APP_NAME>`
**Type:** String  
**Description:** The name of your Azure Web App where the application is deployed.  
**Example:** `fimi-incident-form-genai`  
**Required:** Yes

### `<AZURE_RESOURCE_GROUP>`
**Type:** String  
**Description:** The Azure resource group containing your Web App.  
**Example:** `myResourceGroup`  
**Required:** Yes

## OpenAI Configuration

### `<OPENAI_API_KEY>`
**Type:** API Key  
**Description:** Your OpenAI API key for ChatGPT text generation.  
**Format:** `sk-proj-...`  
**Where to Get:** [OpenAI Platform](https://platform.openai.com/api-keys)  
**Required:** Yes (for `/generate-text` endpoint)

## Google Sheets Configuration

### `<GOOGLE_SHEETS_SPREADSHEET_ID>`
**Type:** String  
**Description:** The ID of your Google Sheet (found in the URL).  
**Example:** `1ANQ1KwhBRIFVEVNnZm-2JHazh7YkWVoubIdlD1ePYF8`  
**Required:** Yes (for Google Sheets endpoints)

### `<GOOGLE_SHEETS_SHEET_NAME>`
**Type:** String  
**Description:** The name of the specific sheet/tab within your spreadsheet.  
**Example:** `Working Sheet` or `Sheet1`  
**Required:** Yes

### `<GOOGLE_SHEETS_SHEET_GID>`
**Type:** Integer  
**Description:** The GID (grid ID) of the specific sheet (found in URL after `#gid=`).  
**Example:** `2098282666`  
**Required:** Yes

### `<GOOGLE_SHEETS_FOLDER_ID>`
**Type:** String  
**Description:** Google Drive folder ID where new sheets should be created (optional).  
**Example:** `1_XxezTATyPbzvXGP23umDDIiIla8vZHB`  
**Required:** No (leave empty if not using sheet creation features)

### `<GOOGLE_SHEETS_OWNER_EMAIL>`
**Type:** Email  
**Description:** Email address to set as owner when creating new Google Sheets.  
**Example:** `user@example.com`  
**Required:** No (only needed for sheet creation features)

## Google Service Account Configuration

Create a service account in [Google Cloud Console](https://console.cloud.google.com/) and download the JSON key file.

### `<GOOGLE_PROJECT_ID>`
**Type:** String  
**Description:** Your Google Cloud project ID.  
**Example:** `my-project-123456`  
**JSON Key Field:** `project_id`  
**Required:** Yes

### `<GOOGLE_PRIVATE_KEY_ID>`
**Type:** String  
**Description:** The private key ID from your service account.  
**Example:** `9cd2ee37cb34cd697e615e3ce917361124c0870f`  
**JSON Key Field:** `private_key_id`  
**Required:** Yes

### `<GOOGLE_CLIENT_EMAIL>`
**Type:** Email  
**Description:** The service account email address.  
**Example:** `my-service-account@my-project.iam.gserviceaccount.com`  
**JSON Key Field:** `client_email`  
**Required:** Yes

### `<GOOGLE_CLIENT_ID>`
**Type:** String  
**Description:** The service account client ID.  
**Example:** `104167410744263934390`  
**JSON Key Field:** `client_id`  
**Required:** Yes

### `<GOOGLE_CLIENT_CERT_URL>`
**Type:** URL  
**Description:** The X.509 certificate URL for the service account.  
**Example:** `https://www.googleapis.com/robot/v1/metadata/x509/my-service-account%40my-project.iam.gserviceaccount.com`  
**JSON Key Field:** `client_x509_cert_url`  
**Required:** Yes

### Google Service Account Private Key
**Note:** The private key is set separately in the PowerShell script using the Azure REST API because it's a multi-line value. It reads from `secrets/service_account.json`.

## Google OAuth Configuration

Create OAuth 2.0 credentials in [Google Cloud Console](https://console.cloud.google.com/apis/credentials).

### `<GOOGLE_OAUTH_CLIENT_ID>`
**Type:** String  
**Description:** OAuth 2.0 Client ID for Google authentication.  
**Example:** `123456789-abcdefg.apps.googleusercontent.com`  
**Required:** No (only if using OAuth features)

### `<GOOGLE_OAUTH_CLIENT_SECRET>`
**Type:** String  
**Description:** OAuth 2.0 Client Secret.  
**Example:** `GOCSPX-aBcDeFgHiJkLmNoPqRsTuVwXyZ`  
**Required:** No (only if using OAuth features)

## Auto-Archiver S3 Storage Configuration

For storing archived content in AWS S3.

### `<S3_BUCKET_NAME>`
**Type:** String  
**Description:** The name of your S3 bucket for storing archives.  
**Example:** `auto-archiver-s3-bucket`  
**Required:** Yes (for Auto-Archiver functionality)

### `<S3_REGION>`
**Type:** String  
**Description:** The AWS region where your S3 bucket is located.  
**Example:** `us-east-1`  
**Required:** Yes

### `<S3_ACCESS_KEY>`
**Type:** String  
**Description:** AWS IAM access key ID with S3 permissions.  
**Example:** `AKIAIOSFODNN7EXAMPLE`  
**Where to Get:** [AWS IAM Console](https://console.aws.amazon.com/iam/)  
**Required:** Yes

### `<S3_SECRET_KEY>`
**Type:** String (Secret)  
**Description:** AWS IAM secret access key.  
**Example:** `wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY`  
**Required:** Yes

### `<S3_ENDPOINT_URL>`
**Type:** URL  
**Description:** The S3 endpoint URL.  
**Example:** `https://s3.us-east-1.amazonaws.com`  
**Required:** Yes

### `<S3_CDN_URL>`
**Type:** URL Template  
**Description:** CDN URL pattern for accessing archived content (use `{key}` as placeholder).  
**Example:** `https://auto-archiver-s3-bucket.s3.us-east-1.amazonaws.com/{key}`  
**Required:** Yes

## Auto-Archiver Google Drive Storage Configuration

Alternative to S3 for storing archived content.

### `<GDRIVE_ROOT_FOLDER_ID>`
**Type:** String  
**Description:** Google Drive folder ID where archives should be stored.  
**Example:** `1_XxezTATyPbzvXGP23umDDIiIla8vZHB`  
**Where to Get:** From the folder URL in Google Drive  
**Required:** No (if using S3 instead)

## Auto-Archiver Wayback Machine Configuration

For enhanced archiving with Internet Archive's Wayback Machine.

### `<WAYBACK_API_KEY>`
**Type:** String  
**Description:** Internet Archive S3 API access key.  
**Where to Get:** [Internet Archive Account Settings](https://archive.org/account/s3.php)  
**Required:** No (optional enhancement)

### `<WAYBACK_API_SECRET>`
**Type:** String (Secret)  
**Description:** Internet Archive S3 API secret key.  
**Required:** No (optional enhancement)

## Azure Container Instance Configuration

For running Auto-Archiver in Azure Container Instances.

### `<AZURE_SUBSCRIPTION_ID>`
**Type:** GUID  
**Description:** Your Azure subscription ID.  
**Example:** `a911bdaa-e084-46e8-936f-f793e572f245`  
**Where to Get:** `az account show --query id`  
**Required:** Yes (for ACI-based Auto-Archiver)

### `<ACI_RESOURCE_GROUP_NAME>`
**Type:** String  
**Description:** The resource group where container instances will be created.  
**Example:** `myresourcegroup`  
**Required:** Yes

### `<ACI_LOCATION>`
**Type:** String  
**Description:** Azure region for container instances.  
**Example:** `australiacentral`, `eastus`, `westeurope`  
**Required:** Yes

### `<ACI_CONTAINER_IMAGE>`
**Type:** String  
**Description:** Full container image reference with tag or digest.  
**Example:** `myregistry.azurecr.io/auto-archiver-custom:latest` or with digest `@sha256:abc123...`  
**Required:** Yes

### `<ACR_REGISTRY_SERVER>`
**Type:** String  
**Description:** Azure Container Registry server name.  
**Example:** `myregistry.azurecr.io`  
**Required:** Yes (if using private registry)

### `<ACR_USERNAME>`
**Type:** String  
**Description:** Azure Container Registry username.  
**Example:** `myregistry`  
**Required:** Yes (if using private registry)

### `<ACR_PASSWORD>`
**Type:** String (Secret)  
**Description:** Azure Container Registry password or access token.  
**Where to Get:** Azure Portal → Container Registry → Access keys  
**Required:** Yes (if using private registry)

## Azure Storage Configuration

For persistent storage with Azure Container Instances.

### `<AZURE_STORAGE_ACCOUNT_NAME>`
**Type:** String  
**Description:** The name of your Azure Storage account.  
**Example:** `mystorageaccount`  
**Required:** Yes (for ACI file share mounting)

### `<AZURE_STORAGE_ACCOUNT_KEY>`
**Type:** String (Secret)  
**Description:** Access key for the Azure Storage account.  
**Where to Get:** Azure Portal → Storage Account → Access keys  
**Required:** Yes

## Azure Log Analytics Configuration

For monitoring and logging container execution.

### `<LOG_ANALYTICS_WORKSPACE_ID>`
**Type:** GUID  
**Description:** The workspace ID of your Log Analytics workspace.  
**Example:** `c76a0d30-b7f8-4d08-a8ce-555ba19535e5`  
**Where to Get:** Azure Portal → Log Analytics Workspace → Properties  
**Required:** Yes (for container logging)

### `<LOG_ANALYTICS_WORKSPACE_KEY>`
**Type:** String (Secret)  
**Description:** Primary or secondary key for Log Analytics.  
**Where to Get:** Azure Portal → Log Analytics Workspace → Agents  
**Required:** Yes

## Configuration Priority

1. **Required for Basic Operation:**
   - Azure App Name & Resource Group
   - OpenAI API Key
   - Google Service Account credentials

2. **Required for Google Sheets Features:**
   - All Google Sheets configuration
   - Google Service Account credentials

3. **Required for Auto-Archiver (Choose One Storage):**
   - Either S3 Storage configuration OR Google Drive configuration
   - Azure Container Instance configuration
   - Azure Storage configuration
   - Azure Log Analytics configuration

4. **Optional Enhancements:**
   - Google OAuth (for alternative authentication)
   - Wayback Machine credentials (for enhanced archiving)

## Security Best Practices

⚠️ **NEVER commit the actual configured `configure-azure-app-settings.ps1` file with real values to version control!**

1. Keep the template version (with placeholders) in Git
2. Create a local copy with your actual values (add to `.gitignore`)
3. Use Azure Key Vault for production secrets
4. Rotate credentials regularly
5. Use managed identities where possible

## Updating Configuration

After updating settings with the PowerShell script, restart your Azure Web App:

```powershell
az webapp restart --name <AZURE_APP_NAME> --resource-group <AZURE_RESOURCE_GROUP>
```
