// Today in Space: everything the site runs on, in one resource group.
// See infra/README.md before deploying. App settings are replaced as a whole set, so run what-if first.

targetScope = 'resourceGroup'

@description('Azure region for every resource.')
param location string = resourceGroup().location

// ---------- Storage ----------

@description('Storage account that holds the daily digests and archived images. Every past day lives here.')
param storageAccountName string

@description('Storage SKU.')
param storageSku string = 'Standard_LRS' // VERIFY: redundancy of the live todayinspace account

param digestsContainerName string = 'digests'
param imagesContainerName string = 'images'

// ---------- Web app ----------

param webAppName string
param webPlanName string // VERIFY: live plan name is unknown

@description('App Service plan SKU for the web app, e.g. F1, D1, B1.')
param webPlanSku string = 'F1' // VERIFY: likely Free/Shared, since the site has no custom domain

@description('Always On is not available on Free/Shared plans.')
param webAlwaysOn bool = false // VERIFY

// ---------- Function app ----------

param functionAppName string
param functionPlanName string // VERIFY: live Consumption plan name is unknown

@description('Storage account the Functions runtime uses (AzureWebJobsStorage and the content share). Often a separate account created alongside the function app.')
param functionStorageAccountName string = storageAccountName // VERIFY

@description('WEBSITE_CONTENTSHARE. The deployed function code lives in this file share, so it must match the live value exactly.')
@minLength(3)
param functionContentShare string

@secure()
@description('NASA API key for the daily fetch.')
param nasaApiKey string

// ---------- Application Insights ----------

@description('Create a new Log Analytics workspace and Application Insights and connect both apps to it.')
param appInsights bool = false

@description('Daily ingestion cap for the new Log Analytics workspace, in GB. A string because Bicep has no decimal numbers.')
param dailyQuotaGb string = '0.1'

@secure()
@description('Existing Application Insights connection string already set on the web app. Keeps a deploy from removing it. Ignored when appInsights is true.')
param existingWebAppInsightsConnectionString string = ''

@secure()
@description('Existing Application Insights connection string already set on the function app. Keeps a deploy from removing it. Ignored when appInsights is true.')
param existingFunctionAppInsightsConnectionString string = ''

// ---------- Storage resources ----------

// Security defaults: no anonymous blob access, HTTPS only, TLS 1.2. Images are served through the web app's
// /images/{name} route, so nothing needs public containers.
var storageSecurity = {
  allowBlobPublicAccess: false // VERIFY: confirm this is already off on the live account
  minimumTlsVersion: 'TLS1_2'
  supportsHttpsTrafficOnly: true
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  kind: 'StorageV2'
  sku: { name: storageSku }
  properties: storageSecurity
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource digestsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: digestsContainerName
  properties: { publicAccess: 'None' }
}

resource imagesContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: imagesContainerName
  properties: { publicAccess: 'None' }
}

// Only declared when the Functions runtime uses its own account. Same defaults as the main one.
var separateFunctionStorage = functionStorageAccountName != storageAccountName

resource functionStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = if (separateFunctionStorage) {
  name: functionStorageAccountName
  location: location
  kind: 'StorageV2' // VERIFY: if separate, its kind and SKU
  sku: { name: 'Standard_LRS' }
  properties: storageSecurity
}

// Connection strings are built here from the account keys, so no secret is ever written to a file.
var storageConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'

var functionStorageConnectionString = separateFunctionStorage
  ? 'DefaultEndpointsProtocol=https;AccountName=${functionStorageAccountName};AccountKey=${functionStorage!.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
  : storageConnectionString

// ---------- Application Insights resources ----------

resource logWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = if (appInsights) {
  name: '${webAppName}-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    // Hard daily cap so telemetry can never grow past the free allowance.
    workspaceCapping: { dailyQuotaGb: json(dailyQuotaGb) }
  }
}

resource appInsightsComponent 'Microsoft.Insights/components@2020-02-02' = if (appInsights) {
  name: '${webAppName}-insights'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logWorkspace.id
  }
}

var newAppInsightsConnectionString = appInsights ? appInsightsComponent!.properties.ConnectionString : ''
var webAppInsightsConnectionString = appInsights ? newAppInsightsConnectionString : existingWebAppInsightsConnectionString
var functionAppInsightsConnectionString = appInsights ? newAppInsightsConnectionString : existingFunctionAppInsightsConnectionString

// Both apps only turn on telemetry when this setting exists, so leave it out entirely when there's no value.
var webAppInsightsSettings = empty(webAppInsightsConnectionString) ? [] : [
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: webAppInsightsConnectionString }
]
var functionAppInsightsSettings = empty(functionAppInsightsConnectionString) ? [] : [
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: functionAppInsightsConnectionString }
]

// ---------- Web app resources ----------

resource webPlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: webPlanName
  location: location
  sku: { name: webPlanSku }
  properties: {
    reserved: false // Windows
  }
}

resource webApp 'Microsoft.Web/sites@2024-04-01' = {
  name: webAppName
  location: location
  kind: 'app'
  properties: {
    serverFarmId: webPlan.id
    httpsOnly: true
    siteConfig: {
      netFrameworkVersion: 'v10.0'
      metadata: [{ name: 'CURRENT_STACK', value: 'dotnet' }]
      alwaysOn: webAlwaysOn
      minTlsVersion: '1.2'
      ftpsState: 'Disabled' // VERIFY: may change live behavior if FTP deploys were ever used
      appSettings: concat([
        { name: 'Storage__ConnectionString', value: storageConnectionString }
        { name: 'Storage__ContainerName', value: digestsContainerName }
        { name: 'Storage__ImagesContainerName', value: imagesContainerName }
      ], webAppInsightsSettings)
    }
  }
}

// ---------- Function app resources ----------

resource functionPlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: functionPlanName
  location: location
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  properties: {
    reserved: false // Windows
  }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp'
  properties: {
    serverFarmId: functionPlan.id
    httpsOnly: true
    siteConfig: {
      netFrameworkVersion: 'v10.0'
      minTlsVersion: '1.2'
      ftpsState: 'Disabled' // VERIFY: may change live behavior if FTP deploys were ever used
      appSettings: concat([
        { name: 'AzureWebJobsStorage', value: functionStorageConnectionString }
        // Windows Consumption apps keep their deployed code in this Azure Files share.
        { name: 'WEBSITE_CONTENTAZUREFILECONNECTIONSTRING', value: functionStorageConnectionString }
        { name: 'WEBSITE_CONTENTSHARE', value: functionContentShare }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'Storage__ConnectionString', value: storageConnectionString }
        { name: 'Storage__ContainerName', value: digestsContainerName }
        { name: 'Storage__ImagesContainerName', value: imagesContainerName }
        { name: 'NASA_API_KEY', value: nasaApiKey }
      ], functionAppInsightsSettings)
    }
  }
}

// Names and hostnames only. Keys and connection strings are never output.
output webAppHostName string = webApp.properties.defaultHostName
output functionAppName string = functionApp.name
