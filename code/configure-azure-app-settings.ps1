# Azure App Settings Configuration Commands
# Run these commands to configure all sensitive values in Azure App Settings
# Replace the placeholder values with your actual credentials

# App name
$appName = "<AZURE_APP_NAME>"
$resourceGroup = "<AZURE_RESOURCE_GROUP>"

# OpenAI Configuration
az webapp config appsettings set --name $appName --resource-group $resourceGroup --settings `
  "OpenAIKey=<OPENAI_API_KEY>"

# Google Sheets Configuration
az webapp config appsettings set --name $appName --resource-group $resourceGroup --settings `
  "GoogleSheets__SpreadsheetId=<GOOGLE_SHEETS_SPREADSHEET_ID>" `
  "GoogleSheets__SheetName=<GOOGLE_SHEETS_SHEET_NAME>" `
  "GoogleSheets__Sheet_gid=<GOOGLE_SHEETS_SHEET_GID>" `
  "GoogleSheets__FolderID=<GOOGLE_SHEETS_FOLDER_ID>" `
  "GoogleSheets__OwnerEmail=<GOOGLE_SHEETS_OWNER_EMAIL>"

# Google Service Account Configuration
az webapp config appsettings set --name $appName --resource-group $resourceGroup --settings `
  "GoogleSheets__ServiceAccount__project_id=<GOOGLE_PROJECT_ID>" `
  "GoogleSheets__ServiceAccount__private_key_id=<GOOGLE_PRIVATE_KEY_ID>" `
  "GoogleSheets__ServiceAccount__client_email=<GOOGLE_CLIENT_EMAIL>" `
  "GoogleSheets__ServiceAccount__client_id=<GOOGLE_CLIENT_ID>" `
  "GoogleSheets__ServiceAccount__client_x509_cert_url=<GOOGLE_CLIENT_CERT_URL>"

# NOTE: Private key is set separately at the end using Azure REST API due to Azure CLI limitations with multi-line values

# Google OAuth Configuration
az webapp config appsettings set --name $appName --resource-group $resourceGroup --settings `
  "GoogleOAuth__ClientId=<GOOGLE_OAUTH_CLIENT_ID>" `
  "GoogleOAuth__ClientSecret=<GOOGLE_OAUTH_CLIENT_SECRET>"

# Auto-Archiver Azure Container Configuration
az webapp config appsettings set --name $appName --resource-group $resourceGroup --settings `
  "AutoArchiver__ContainerMaxWaitMinutes=180"

# Auto-Archiver S3 Storage Configuration
az webapp config appsettings set --name $appName --resource-group $resourceGroup --settings `
  "AutoArchiver__S3Storage__Bucket=<S3_BUCKET_NAME>" `
  "AutoArchiver__S3Storage__Region=<S3_REGION>" `
  "AutoArchiver__S3Storage__Key=<S3_ACCESS_KEY>" `
  "AutoArchiver__S3Storage__Secret=<S3_SECRET_KEY>" `
  "AutoArchiver__S3Storage__EndpointUrl=<S3_ENDPOINT_URL>" `
  "AutoArchiver__S3Storage__CdnUrl=<S3_CDN_URL>"

# Auto-Archiver Google Drive Storage Configuration
az webapp config appsettings set --name $appName --resource-group $resourceGroup --settings `
  "AutoArchiver__GDriveStorage__RootFolderId=<GDRIVE_ROOT_FOLDER_ID>"

# Auto-Archiver Wayback Enricher Configuration
az webapp config appsettings set --name $appName --resource-group $resourceGroup --settings `
  "AutoArchiver__WaybackEnricher__Key=<WAYBACK_API_KEY>" `
  "AutoArchiver__WaybackEnricher__Secret=<WAYBACK_API_SECRET>"

# Azure Container Instance Configuration
az webapp config appsettings set --name $appName --resource-group $resourceGroup --settings `
  "AzureContainerInstance__SubscriptionId=<AZURE_SUBSCRIPTION_ID>" `
  "AzureContainerInstance__ResourceGroupName=<ACI_RESOURCE_GROUP_NAME>" `
  "AzureContainerInstance__Location=<ACI_LOCATION>" `
  "AzureContainerInstance__ContainerImage=<ACI_CONTAINER_IMAGE>" `
  "AzureContainerInstance__RegistryServer=<ACR_REGISTRY_SERVER>" `
  "AzureContainerInstance__RegistryUsername=<ACR_USERNAME>" `
  "AzureContainerInstance__RegistryPassword=<ACR_PASSWORD>"

# Azure Storage Configuration
az webapp config appsettings set --name $appName --resource-group $resourceGroup --settings `
  "AzureStorage__AccountName=<AZURE_STORAGE_ACCOUNT_NAME>" `
  "AzureStorage__AccountKey=<AZURE_STORAGE_ACCOUNT_KEY>"

# Azure Log Analytics Configuration
az webapp config appsettings set --name $appName --resource-group $resourceGroup --settings `
  "AzureLogAnalytics__WorkspaceId=<LOG_ANALYTICS_WORKSPACE_ID>" `
  "AzureLogAnalytics__WorkspaceKey=<LOG_ANALYTICS_WORKSPACE_KEY>"

Write-Host "All App Settings configured successfully!" -ForegroundColor Green

# Now set the private key using Azure REST API (Azure CLI can't handle multi-line values properly)
Write-Host ""
Write-Host "Setting Google Service Account private key via REST API..." -ForegroundColor Cyan

$subscriptionId = (az account show --query id -o tsv)
$privateKey = (Get-Content .\secrets\service_account.json | ConvertFrom-Json).private_key

# Get current app settings to merge with private key
$currentSettings = az webapp config appsettings list --name $appName --resource-group $resourceGroup | ConvertFrom-Json
$settingsHash = @{}
foreach ($setting in $currentSettings) {
    $settingsHash[$setting.name] = $setting.value
}

# Add/update the private key
$settingsHash["GoogleSheets__ServiceAccount__private_key"] = $privateKey

# Create JSON body with all settings
$body = @{properties=$settingsHash} | ConvertTo-Json -Depth 10
$body | Out-File -Encoding utf8 -NoNewline app-settings-update.json

# Apply via REST API
az rest --method PUT --uri "https://management.azure.com/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.Web/sites/$appName/config/appsettings?api-version=2022-03-01" --body "@app-settings-update.json" --output none

Remove-Item app-settings-update.json

# Verify private key length
$storedLength = (az webapp config appsettings list --name $appName --resource-group $resourceGroup --query "[?name=='GoogleSheets__ServiceAccount__private_key'].value" -o tsv).Length
if ($storedLength -gt 1600) {
    Write-Host "Private key set successfully! ($storedLength characters)" -ForegroundColor Green
} else {
    Write-Host "WARNING: Private key may not be set correctly (only $storedLength characters)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Configuration complete!" -ForegroundColor Green
Write-Host ""
Write-Host "Next Steps:" -ForegroundColor Cyan
Write-Host "1. Restart the web app: az webapp restart --name $appName --resource-group $resourceGroup"
Write-Host "2. Test your endpoints"
Write-Host "3. Monitor logs for authentication success"

