# Infrastructure

Everything Today in Space runs on, described in Bicep so it can be rebuilt or moved to a new subscription with one command.

- `main.bicep` is the template (deploys into one resource group).
- `main.bicepparam` describes the original student site. It has no secrets. Those come from environment variables at deploy time.
- `company.bicepparam` describes the new home in the company subscription (see [Company subscription (2026-10)](#company-subscription-2026-10)).

> [!WARNING]
> **App settings are all-or-nothing.** Deploying `siteConfig.appSettings` replaces the app's whole set of settings.
> Any setting that's live in Azure but not in this template gets **deleted**, including Application Insights
> and the function's content share. Always run `what-if` first, and deploy only after it shows no unexpected
> removals or changes.

## What each resource is for

| Resource | Name | Why it exists |
| --- | --- | --- |
| Storage account | `todayinspace` | Holds every day's digest and archived image. **Irreplaceable data.** |
| Blob container | `digests` | One JSON file per day (`YYYY-MM-DD.json`) plus `latest.json`. Private. |
| Blob container | `images` | Copies of each day's APOD image, served through the site's `/images/{name}` route. Private. |
| App Service plan | `webPlanName` | Hosts the web app (Windows). |
| Web app | `tis-app-east` | The ASP.NET Core MVC site. |
| Consumption plan (Y1) | `functionPlanName` | Pay-per-run hosting for the function app. |
| Function app | `tis-function` | Fetches APOD and NOAA data twice a day and writes it to storage. |
| Function runtime storage | `functionStorageAccountName` | Where the Functions host keeps its state and the deployed code (content share). Defaults to `todayinspace`. |
| Log Analytics + App Insights | `*-logs`, `*-insights` | Only created when `appInsights = true`. The workspace has a daily cap (`dailyQuotaGb`, 0.1 GB) so it stays in the free tier. |

## Values to confirm before the first run

Anything marked `// VERIFY` in `main.bicep` or `main.bicepparam` is a best guess. Check each one against the portal:

- Both App Service plan names, and the web plan's SKU (`F1` assumed) and Always On (off assumed).
- Whether `AzureWebJobsStorage` points at `todayinspace` or a separate account.
- `WEBSITE_CONTENTSHARE` on `tis-function`. It has no default on purpose, because the deployed code lives in that share.
- Whether either app already has `APPLICATIONINSIGHTS_CONNECTION_STRING` set.
- Storage redundancy (`Standard_LRS` assumed), and that blob public access is already off.
- FTPS is set to `Disabled` and TLS to 1.2 on both apps. If either is different live, what-if will show it.

## Preview and deploy

Run these yourself from the repo root, signed in with `az login`. Values come from environment variables so nothing secret lands in shell history or files. Below is PowerShell. In bash, use `export NAME=value`.

```powershell
$env:NASA_API_KEY = "<NASA API key>"
$env:TIS_FUNCTION_CONTENT_SHARE = "<live WEBSITE_CONTENTSHARE value>"
# Only if the apps already have App Insights connected (copy from each app's settings):
$env:TIS_WEB_APPINSIGHTS_CONNECTION_STRING = "<web app's value>"
$env:TIS_FUNCTION_APPINSIGHTS_CONNECTION_STRING = "<function app's value>"
```

Preview what would change (makes no changes):

```bash
az deployment group what-if --resource-group <resource-group> --parameters infra/main.bicepparam
```

Read the output closely, especially any `Delete` or `Modify` under `appSettings`. Then deploy:

```bash
az deployment group create --resource-group <resource-group> --parameters infra/main.bicepparam
```

CI only compiles the template (`az bicep build`). It never deploys infrastructure.

## Moving to a new subscription

1. **Create the resource group** in the new subscription:
   `az group create --name <new-resource-group> --location eastus`
2. **Pick names.** Storage account and app names are globally unique, so the old ones can't be reused while the old resources exist. Update `main.bicepparam`. For a fresh function app, `TIS_FUNCTION_CONTENT_SHARE` can be any new lowercase name (for example `tis-function-content`).
3. **Deploy** with the `what-if` and `create` commands above, pointed at the new resource group.
4. **Copy the blobs.** This is the step that matters: `digests` and `images` hold every past day. Use azcopy with a short-lived, read-only SAS on the source and a write SAS on the destination:
   ```bash
   azcopy copy "https://todayinspace.blob.core.windows.net/digests?<source-SAS>" "https://<new-account>.blob.core.windows.net/digests?<dest-SAS>" --recursive
   azcopy copy "https://todayinspace.blob.core.windows.net/images?<source-SAS>" "https://<new-account>.blob.core.windows.net/images?<dest-SAS>" --recursive
   ```
   Check that the blob counts match before going further, and keep the old account until the new site is confirmed working.
5. **Update GitHub** (repo Settings, then Secrets and variables, then Actions):
   - Variables `AZURE_WEBAPP_NAME` and `AZURE_FUNCTIONAPP_NAME` set to the new app names.
   - Secrets `AZURE_WEBAPP_PUBLISH_PROFILE` and `AZURE_FUNCTIONAPP_PUBLISH_PROFILE` set to the new apps' publish profiles (download each from the app's Overview page).
6. **Redeploy the code** by re-running the CI/CD workflow on `main` (Run workflow), then check `/healthz` on the new hostname.

## Company subscription (2026-10)

The site moves from the student account into the company subscription. It's built fresh from `main.bicep`
with `company.bicepparam`, in its own resource group `rg-todayinspace` in West US 2. The new names are:

| Resource | Name |
| --- | --- |
| Storage account | `tisdatagdv7m` (also the function's runtime storage) |
| Web app (Linux, B1, Always On) | `tis-web-gdv7m` on plan `tis-web-plan` |
| Function app (Windows Consumption) | `tis-func-gdv7m` on plan `tis-func-plan` |

The old student site keeps running the whole time. Nothing gets turned off until the new one is confirmed working.

The commands below are PowerShell, run from the repo root. All of it also works in
[Azure Cloud Shell](https://shell.azure.com) (bash), which already has `az` and `azcopy` installed. Clone the repo
there first, and use `export NAME=value` instead of `$env:NAME = "value"`.

1. **Sign in and pick the company subscription.**
   ```powershell
   az login
   az account set --subscription <company-subscription-id>
   ```
2. **Create the resource group.**
   ```powershell
   az group create --name rg-todayinspace --location westus2
   ```
3. **Set the NASA key** in the same terminal you'll deploy from. `company.bicepparam` reads it with
   `readEnvironmentVariable`, which stops with an error if `NASA_API_KEY` isn't set. That's on purpose: it means
   you can't accidentally deploy a function app without the key.
   ```powershell
   $env:NASA_API_KEY = "<NASA API key>"
   ```
4. **Preview.** This makes no changes. Everything should show as `Create`. If a name is already taken somewhere
   in Azure, this is where you'll find out.
   ```powershell
   az deployment group what-if --resource-group rg-todayinspace --parameters infra/company.bicepparam
   ```
5. **Create everything.**
   ```powershell
   az deployment group create --resource-group rg-todayinspace --parameters infra/company.bicepparam
   ```
6. **Copy the blobs.** This is the step that matters: `digests` and `images` hold every past day. You need a
   short-lived, read-only SAS on the old `todayinspace` account and a write SAS on `tisdatagdv7m`. Make each one
   in the portal (storage account, then Shared access signature).
   ```powershell
   azcopy copy "https://todayinspace.blob.core.windows.net/digests?<source-SAS>" "https://tisdatagdv7m.blob.core.windows.net/digests?<dest-SAS>" --recursive
   azcopy copy "https://todayinspace.blob.core.windows.net/images?<source-SAS>" "https://tisdatagdv7m.blob.core.windows.net/images?<dest-SAS>" --recursive
   ```
   Check that the counts match on both sides, for each container:
   ```powershell
   azcopy list "https://todayinspace.blob.core.windows.net/digests?<source-SAS>" --running-tally
   azcopy list "https://tisdatagdv7m.blob.core.windows.net/digests?<dest-SAS>" --running-tally
   ```
7. **Point GitHub at the new apps** (repo Settings, then Secrets and variables, then Actions):
   - Variable `AZURE_WEBAPP_NAME` = `tis-web-gdv7m`
   - Variable `AZURE_FUNCTIONAPP_NAME` = `tis-func-gdv7m`
   - Secret `AZURE_WEBAPP_PUBLISH_PROFILE` = the publish profile from `tis-web-gdv7m` (Overview page, Download publish profile)
   - Secret `AZURE_FUNCTIONAPP_PUBLISH_PROFILE` = the publish profile from `tis-func-gdv7m`

   From here on, merges to `main` deploy to the new apps only.
8. **Deploy the code.** In GitHub Actions, open the CI/CD workflow and use Run workflow on `main`.
9. **Fill in people in space.** In the portal, open `tis-func-gdv7m`, then Functions, then `RefreshPeopleInSpace`,
   then Code + Test, and click Test/Run once. Otherwise the count stays empty until its next timer run (every 3 hours).
10. **Check the new site** at `https://tis-web-gdv7m.azurewebsites.net`:
    - `/healthz` reports healthy
    - the home page shows today's picture and space weather
    - `/Home/Archive` opens a past day
    - `/api/sky/people` returns the people in space
11. **Re-sync just before switching over.** The old function keeps writing new digests and images into the old
    account until you switch, so copy anything new across one last time:
    ```powershell
    azcopy sync "https://todayinspace.blob.core.windows.net/digests?<source-SAS>" "https://tisdatagdv7m.blob.core.windows.net/digests?<dest-SAS>" --recursive
    azcopy sync "https://todayinspace.blob.core.windows.net/images?<source-SAS>" "https://tisdatagdv7m.blob.core.windows.net/images?<dest-SAS>" --recursive
    ```
    `sync` only copies what's new or changed, and it never deletes anything in the new account.
