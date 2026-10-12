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
| Resource group | `rg-todayinspace` (West US 2) | Holds everything below, in the company subscription. |
| Storage account | `tisdatagdv7m` | Holds every day's digest and archived image. **Irreplaceable data.** Also the function's runtime storage (state and the `tis-func-content` share). |
| Blob container | `digests` | One JSON file per day (`YYYY-MM-DD.json`) plus `latest.json`. Private. |
| Blob container | `images` | Copies of each day's APOD image, served through the site's `/images/{name}` route. Private. |
| App Service plan | `tis-web-plan` | Hosts the web app (Linux, B1, Always On). |
| Web app | `tis-web-gdv7m` | The ASP.NET Core MVC site, at `space.evergreensystemswa.com`. |
| Custom domain + certificate | `space.evergreensystemswa.com` | Set up by hand in the portal, not in Bicep (see [Gotchas we hit](#gotchas-we-hit)). A free App Service Managed Certificate, bound with SNI. |
| Consumption plan (Y1) | `tis-func-plan` | Pay-per-run hosting for the function app (Windows). |
| Function app | `tis-func-gdv7m` | Fetches APOD and NOAA data on a timer and writes it to storage. |
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

### Gotchas we hit

Things that tripped us up during the move, so the next one goes faster.

- **SAS tokens.** The portal gives you the SAS without the leading `?`, so add it yourself after the container
  URL. The destination SAS needs Write, Add and Create permissions, not just Read.
- **"Download publish profile" does nothing.** Get it from the CLI instead:
  ```bash
  az webapp deployment list-publishing-profiles --resource-group rg-todayinspace --name tis-web-gdv7m --xml
  ```
- **"Publish profile is invalid for app-name"** wasn't the profile at all. The `AZURE_WEBAPP_NAME` variable still
  had the old name, plus a trailing newline. Re-running the workflow with debug logging turned on shows the
  values it actually uses.
- **Linux web app deploys** with a publish profile need the app setting `WEBSITE_WEBDEPLOY_USE_SCM = true`.
  `main.bicep` sets it on Linux.
- **Portal Test/Run needs CORS.** The function app has to allow `https://portal.azure.com`, which `main.bicep`
  now sets. Without the portal, you can also run a timer function directly with the master key (function app,
  then App keys, then `_master`):
  ```bash
  curl -X POST "https://tis-func-gdv7m.azurewebsites.net/admin/functions/RefreshPeopleInSpace"     -H "x-functions-key: <master-key>" -H "Content-Type: application/json" -d "{}"
  ```
  A `202 Accepted` means it started.
- **Custom domain.** In Cloudflare, add a CNAME `space` pointing to `tis-web-gdv7m.azurewebsites.net`, set to
  **DNS only (grey cloud)** so App Service can validate it, and a TXT record `asuid.space` with the web app's
  Custom Domain Verification ID. Then add the domain on the web app, create a free App Service Managed
  Certificate for it, and bind it with SNI SSL. This lives outside Bicep on purpose: the hostname binding and
  certificate are separate resources, so redeploying the template leaves them alone.
