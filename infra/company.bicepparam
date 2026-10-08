// The company subscription: resource group rg-todayinspace in West US 2, built fresh from main.bicep.
// No secrets here: the NASA key is read from an environment variable at deploy time.
using 'main.bicep'

param location = 'westus2'

param tags = {
  project: 'todayinspace'
}

param storageAccountName = 'tisdatagdv7m'

param webAppName = 'tis-web-gdv7m'
param webPlanName = 'tis-web-plan'
param webOs = 'linux'
param webPlanSku = 'B1'
param webAlwaysOn = true // the reason for leaving F1: no idle unloads

// Stays on Windows Consumption (Y1): pay-per-run, and Linux Consumption is being retired.
param functionAppName = 'tis-func-gdv7m'
param functionPlanName = 'tis-func-plan'
param functionStorageAccountName = 'tisdatagdv7m'

// A fresh app, so this is our own choice rather than a live value to match.
param functionContentShare = 'tis-func-content'

param nasaApiKey = readEnvironmentVariable('NASA_API_KEY')

// Application Insights is its own later step.
param appInsights = false
