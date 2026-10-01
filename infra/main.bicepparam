// Current production values. No secrets here: those are read from environment variables at deploy time.
using 'main.bicep'

param location = 'eastus'

param storageAccountName = 'todayinspace'

param webAppName = 'tis-app-east'
param webPlanName = 'tis-app-east-plan' // VERIFY: copy the real plan name from the portal
param webPlanSku = 'F1' // VERIFY
param webAlwaysOn = false // VERIFY

param functionAppName = 'tis-function'
param functionPlanName = 'tis-function-plan' // VERIFY: copy the real plan name from the portal
param functionStorageAccountName = 'todayinspace' // VERIFY: AzureWebJobsStorage may point at a separate account

// Required and deliberately has no default: it must match the live WEBSITE_CONTENTSHARE exactly.
// It isn't secret, so once it's confirmed in the portal, replace this line with the literal value.
param functionContentShare = readEnvironmentVariable('TIS_FUNCTION_CONTENT_SHARE') // VERIFY

param nasaApiKey = readEnvironmentVariable('NASA_API_KEY')

param appInsights = false
param dailyQuotaGb = '0.1'

// Keep any Application Insights already connected to the apps. Empty means "not connected".
param existingWebAppInsightsConnectionString = readEnvironmentVariable('TIS_WEB_APPINSIGHTS_CONNECTION_STRING', '') // VERIFY
param existingFunctionAppInsightsConnectionString = readEnvironmentVariable('TIS_FUNCTION_APPINSIGHTS_CONNECTION_STRING', '') // VERIFY
