# Azure Application Infrastructure Setup Script
# This script creates all Azure resources needed for the FIMI Incident Form Server

# Exit on any error
$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "FIMI Incident Form Server - Azure Setup" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Configuration Variables - UPDATE THESE
$appName = "<AZURE_APP_NAME>"                          # e.g., "fimi-incident-form-genai"
$resourceGroup = "<AZURE_RESOURCE_GROUP>"              # e.g., "fimi-rg"
$aciResourceGroup = "<ACI_RESOURCE_GROUP_NAME>"        # e.g., "fimi-aci-rg" (for container instances)
$location = "<AZURE_LOCATION>"                         # e.g., "eastus", "westeurope", "australiacentral"
$acrName = "<ACR_NAME>"                                # e.g., "fimiacr" (must be globally unique, lowercase, no hyphens)
$storageAccountName = "<STORAGE_ACCOUNT_NAME>"         # e.g., "fimistorage" (must be globally unique, lowercase, no hyphens)
$logAnalyticsName = "<LOG_ANALYTICS_WORKSPACE_NAME>"   # e.g., "fimi-logs"

# Validate configuration
if ($appName -eq "<AZURE_APP_NAME>" -or $resourceGroup -eq "<AZURE_RESOURCE_GROUP>") {
    Write-Host "ERROR: Please update the configuration variables at the top of this script with your actual values." -ForegroundColor Red
    Write-Host "Replace the placeholders with your desired names." -ForegroundColor Red
    exit 1
}

Write-Host "Configuration:" -ForegroundColor Yellow
Write-Host "  App Name:              $appName"
Write-Host "  Resource Group:        $resourceGroup"
Write-Host "  ACI Resource Group:    $aciResourceGroup"
Write-Host "  Location:              $location"
Write-Host "  Container Registry:    $acrName"
Write-Host "  Storage Account:       $storageAccountName"
Write-Host "  Log Analytics:         $logAnalyticsName"
Write-Host ""

# Confirm before proceeding
$confirm = Read-Host "Do you want to proceed with creating these resources? (yes/no)"
if ($confirm -ne "yes") {
    Write-Host "Setup cancelled." -ForegroundColor Yellow
    exit 0
}

Write-Host ""
Write-Host "Starting Azure resource creation..." -ForegroundColor Green
Write-Host ""

# Check if logged in to Azure
Write-Host "[1/8] Checking Azure login..." -ForegroundColor Cyan
$account = az account show 2>$null
if (-not $account) {
    Write-Host "Not logged in to Azure. Please log in..." -ForegroundColor Yellow
    az login
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Failed to log in to Azure." -ForegroundColor Red
        exit 1
    }
}
$subscriptionId = (az account show --query id -o tsv)
Write-Host "Logged in to subscription: $subscriptionId" -ForegroundColor Green
Write-Host ""

# Create main resource group
Write-Host "[2/8] Creating main resource group..." -ForegroundColor Cyan
az group create --name $resourceGroup --location $location
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Failed to create resource group." -ForegroundColor Red
    exit 1
}
Write-Host "Resource group '$resourceGroup' created successfully." -ForegroundColor Green
Write-Host ""

# Create ACI resource group
Write-Host "[3/8] Creating ACI resource group..." -ForegroundColor Cyan
az group create --name $aciResourceGroup --location $location
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Failed to create ACI resource group." -ForegroundColor Red
    exit 1
}
Write-Host "ACI resource group '$aciResourceGroup' created successfully." -ForegroundColor Green
Write-Host ""

# Create Azure Container Registry
Write-Host "[4/8] Creating Azure Container Registry..." -ForegroundColor Cyan
az acr create --name $acrName --resource-group $resourceGroup --sku Basic --admin-enabled true
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Failed to create Container Registry." -ForegroundColor Red
    exit 1
}
Write-Host "Container Registry '$acrName.azurecr.io' created successfully." -ForegroundColor Green

# Get ACR credentials
$acrUsername = (az acr credential show --name $acrName --query username -o tsv)
$acrPassword = (az acr credential show --name $acrName --query passwords[0].value -o tsv)
Write-Host "ACR Username: $acrUsername" -ForegroundColor Yellow
Write-Host "ACR Password: [hidden]" -ForegroundColor Yellow
Write-Host ""

# Create Storage Account
Write-Host "[5/8] Creating Azure Storage Account..." -ForegroundColor Cyan
az storage account create `
    --name $storageAccountName `
    --resource-group $resourceGroup `
    --location $location `
    --sku Standard_LRS `
    --kind StorageV2
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Failed to create Storage Account." -ForegroundColor Red
    exit 1
}
Write-Host "Storage Account '$storageAccountName' created successfully." -ForegroundColor Green

# Get storage account key
$storageKey = (az storage account keys list --account-name $storageAccountName --resource-group $resourceGroup --query [0].value -o tsv)

# Create file share for Auto-Archiver
Write-Host "Creating file share 'auto-archiver-share'..." -ForegroundColor Cyan
az storage share create `
    --name "auto-archiver-share" `
    --account-name $storageAccountName `
    --account-key $storageKey `
    --quota 10
if ($LASTEXITCODE -ne 0) {
    Write-Host "WARNING: Failed to create file share. You may need to create it manually." -ForegroundColor Yellow
} else {
    Write-Host "File share created successfully." -ForegroundColor Green
}
Write-Host ""

# Create Log Analytics Workspace
Write-Host "[6/8] Creating Log Analytics Workspace..." -ForegroundColor Cyan
az monitor log-analytics workspace create `
    --resource-group $resourceGroup `
    --workspace-name $logAnalyticsName `
    --location $location
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Failed to create Log Analytics Workspace." -ForegroundColor Red
    exit 1
}
Write-Host "Log Analytics Workspace '$logAnalyticsName' created successfully." -ForegroundColor Green

# Get Log Analytics credentials
$workspaceId = (az monitor log-analytics workspace show --resource-group $resourceGroup --workspace-name $logAnalyticsName --query customerId -o tsv)
$workspaceKey = (az monitor log-analytics workspace get-shared-keys --resource-group $resourceGroup --workspace-name $logAnalyticsName --query primarySharedKey -o tsv)
Write-Host ""

# Create App Service Plan
Write-Host "[7/8] Creating App Service Plan..." -ForegroundColor Cyan
az appservice plan create `
    --name "$appName-plan" `
    --resource-group $resourceGroup `
    --location $location `
    --sku B1 `
    --is-linux
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Failed to create App Service Plan." -ForegroundColor Red
    exit 1
}
Write-Host "App Service Plan created successfully." -ForegroundColor Green
Write-Host ""

# Create Web App
Write-Host "[8/8] Creating Web App..." -ForegroundColor Cyan
az webapp create `
    --name $appName `
    --resource-group $resourceGroup `
    --plan "$appName-plan" `
    --runtime "DOTNET|8.0"
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Failed to create Web App." -ForegroundColor Red
    exit 1
}
Write-Host "Web App '$appName' created successfully." -ForegroundColor Green
Write-Host ""

# Configure Web App settings
Write-Host "Configuring Web App settings..." -ForegroundColor Cyan

# Enable HTTPS only
az webapp update --name $appName --resource-group $resourceGroup --https-only true

# Set up managed identity for the web app
az webapp identity assign --name $appName --resource-group $resourceGroup

# Get the managed identity principal ID
$principalId = (az webapp identity show --name $appName --resource-group $resourceGroup --query principalId -o tsv)

# Grant web app access to ACR
Write-Host "Granting Web App access to Container Registry..." -ForegroundColor Cyan
az role assignment create `
    --assignee $principalId `
    --role AcrPull `
    --scope "/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.ContainerRegistry/registries/$acrName"

# Grant web app Contributor role on ACI resource group (to create/manage container instances)
Write-Host "Granting Web App permissions to manage Container Instances..." -ForegroundColor Cyan
az role assignment create `
    --assignee $principalId `
    --role Contributor `
    --scope "/subscriptions/$subscriptionId/resourceGroups/$aciResourceGroup"

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "Azure Infrastructure Setup Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""

Write-Host "Summary of Created Resources:" -ForegroundColor Yellow
Write-Host "------------------------------"
Write-Host "Subscription ID:          $subscriptionId"
Write-Host "Resource Group:           $resourceGroup"
Write-Host "ACI Resource Group:       $aciResourceGroup"
Write-Host "Web App URL:              https://$appName.azurewebsites.net"
Write-Host "Container Registry:       $acrName.azurecr.io"
Write-Host "Storage Account:          $storageAccountName"
Write-Host "Log Analytics Workspace:  $logAnalyticsName"
Write-Host ""

Write-Host "Important Credentials (save these securely):" -ForegroundColor Yellow
Write-Host "---------------------------------------------"
Write-Host "ACR Username:             $acrUsername"
Write-Host "ACR Password:             $acrPassword"
Write-Host "Storage Account Key:      $storageKey"
Write-Host "Log Analytics Workspace ID: $workspaceId"
Write-Host "Log Analytics Workspace Key: $workspaceKey"
Write-Host ""

Write-Host "Next Steps:" -ForegroundColor Cyan
Write-Host "----------"
Write-Host "1. Build and push your Docker container image to ACR:"
Write-Host "   docker build -t $acrName.azurecr.io/auto-archiver-custom:latest ."
Write-Host "   docker login $acrName.azurecr.io -u $acrUsername -p $acrPassword"
Write-Host "   docker push $acrName.azurecr.io/auto-archiver-custom:latest"
Write-Host ""
Write-Host "2. Upload orchestration.yaml to the file share:"
Write-Host "   - Go to Azure Portal > Storage Account > File shares > auto-archiver-share"
Write-Host "   - Create folder 'secrets'"
Write-Host "   - Upload your orchestration.yaml file to the secrets folder"
Write-Host ""
Write-Host "3. Configure application settings using configure-azure-app-settings.ps1"
Write-Host "   - Update the placeholders with your actual credentials"
Write-Host "   - Run the script to set all app settings"
Write-Host ""
Write-Host "4. Deploy your application code:"
Write-Host "   - Build: dotnet publish -c Release"
Write-Host "   - Deploy via VS Code Azure extension or:"
Write-Host "   az webapp deployment source config-zip --name $appName --resource-group $resourceGroup --src ./bin/Release/net8.0/publish.zip"
Write-Host ""
Write-Host "5. Verify deployment:"
Write-Host "   - Visit: https://$appName.azurewebsites.net"
Write-Host "   - Check logs: az webapp log tail --name $appName --resource-group $resourceGroup"
Write-Host ""

# Save credentials to a file for reference
$credentialsFile = "azure-credentials.txt"
@"
FIMI Incident Form Server - Azure Credentials
Generated: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
==============================================

Subscription ID:          $subscriptionId
Resource Group:           $resourceGroup
ACI Resource Group:       $aciResourceGroup
Location:                 $location
Web App Name:             $appName
Web App URL:              https://$appName.azurewebsites.net

Container Registry:       $acrName.azurecr.io
ACR Username:             $acrUsername
ACR Password:             $acrPassword

Storage Account:          $storageAccountName
Storage Account Key:      $storageKey

Log Analytics Workspace:  $logAnalyticsName
Workspace ID:             $workspaceId
Workspace Key:            $workspaceKey

IMPORTANT: Keep this file secure and do not commit it to version control!
Add 'azure-credentials.txt' to your .gitignore file.
"@ | Out-File -FilePath $credentialsFile -Encoding UTF8

Write-Host "Credentials saved to: $credentialsFile" -ForegroundColor Yellow
Write-Host "IMPORTANT: Add this file to .gitignore and keep it secure!" -ForegroundColor Red
Write-Host ""
