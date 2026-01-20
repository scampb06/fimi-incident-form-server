This repository contains the ASP.NET/C# code for several endpoints used by the corresponding HTML/JavaScript/CSS in the the FIMI Incident Form.

THIS IS A DRAFT REPOSITORY AWAITING INTERNAL REVIEW AND TESTING. WE ADVISE YOU NOT TO USE IT IN ITS CURRENT STATE.  

## Azure Setup

Before deploying this application, you need to complete the following prerequisites:

### Prerequisites

1. **Azure Subscription** - You must have an active Azure subscription
2. **Azure CLI** - Install the [Azure CLI](https://docs.microsoft.com/cli/azure/install-azure-cli)
3. **External Services** - Set up accounts and obtain credentials for:
   - **OpenAI API**: Get your API key from [platform.openai.com](https://platform.openai.com)
   - **Google Cloud**: Create a project and service account in [Google Cloud Console](https://console.cloud.google.com)
     - Enable Google Sheets API and Google Drive API
     - Create a service account and download the JSON key file
     - Share your Google Sheets with the service account email address
   - **AWS S3** (optional): Create bucket and IAM credentials in [AWS Console](https://console.aws.amazon.com) if using S3 for archive storage
   - **Internet Archive** (optional): Register for API access at [archive.org](https://archive.org) if using Wayback Machine

### Infrastructure Setup

Once you have completed the prerequisites above:

1. **Create Azure Resources** - Run the infrastructure setup script:
   ```powershell
   .\create-azure-app.ps1
   ```
   This script creates:
   - Resource groups (main app and container instances)
   - Azure Web App (App Service + App Service Plan)
   - Azure Container Registry (ACR)
   - Azure Storage Account with file share
   - Log Analytics Workspace
   - Necessary permissions and role assignments

2. **Build and Push Container Image** - After infrastructure is created, build and push the Auto-Archiver container image:
   ```bash
   docker build -t <ACR_NAME>.azurecr.io/auto-archiver-custom:latest .
   docker login <ACR_NAME>.azurecr.io -u <ACR_USERNAME> -p <ACR_PASSWORD>
   docker push <ACR_NAME>.azurecr.io/auto-archiver-custom:latest
   ```

3. **Upload Configuration Files** - Upload orchestration.yaml to Azure Storage:
   - Navigate to Azure Portal → Storage Account → File shares → auto-archiver-share
   - Create a folder named `secrets`
   - Upload your `orchestration.yaml` file to the secrets folder

4. **Configure Application Settings** - Run the configuration script:
   ```powershell
   .\configure-azure-app-settings.ps1
   ```
   Update all placeholders in this script with your actual credentials before running.

5. **Deploy Application** - Deploy your application code:
   ```bash
   dotnet publish -c Release
   # Then deploy via Azure CLI or VS Code Azure extension
   ```

All configuration parameters are documented in [appsettings.md](appsettings.md).

## Endpoints

This application supports the following endpoints:

| Endpoint | Description |
|----------|----------|
| [/generate-text](documentation/generate-text.md) | Uses ChatGPT to generate a text summary given a prompt |
| [/cors-proxy/pdf](documentation/cors-proxy-pdf.md) | bypass CORS (Cross-Origin Resource Sharing) restrictions when PDF server doesn't allow direct browser access |
| [/google-sheets/extract-domains](documentation/google-sheets-extract-domains.md) | Extracts top-level domain for each URL in a given Google Sheet |
| [/google-sheets/extract-channels](documentation/google-sheets-extract-channels.md) | Extract channel for each URL in a given Google Sheet |
| [/google-sheets/archive-urls](documentation/google-sheets-archive-urls.md) | Archives URLs in a given Google Sheet using Wayback Machine |
| [/google-sheets/check-permissions](documentation/google-sheets-check-permissions.md) | Checks if service account has read (or optionally write) permission to access a given Google Sheet |
| [/google-sheets/data-for-url](documentation/google-sheets-data-for-url.md) | Uses service account to retrieve all data from given Google Sheet |
| [/bellingcat/auto-archiver-sheets-asynchronous](documentation/bellingcat-auto-archiver-sheets-asynchronous.md) | Runs background job to archive URLs in a given Google Sheet using Bellingcat Auto Archiver |
| [/bellingcat/auto-archiver/status/{jobId}](documentation/bellingcat-auto-archiver-status.md) | Check status of Bellingcat Auto Archiver background job  |