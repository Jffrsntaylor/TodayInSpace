# Today in Space

A daily astronomy and space-weather digest. Each morning a scheduled Azure Function pulls NASA's Astronomy Picture of the Day and live space-weather data from NOAA's Space Weather Prediction Center, saves a snapshot, and an ASP.NET Core MVC site displays it — including an archive of every past day.

## Features

- **Astronomy Picture of the Day** with title, explanation, and credit
- **Space weather at a glance**: current Kp index, aurora chance, and solar wind speed, color-coded by severity
- **3-day Kp forecast** bar chart
- **Live Sky**: an interactive 3D globe (drag to spin) showing the International Space Station and the Hubble Space Telescope in real time at true altitude, with their orbits, NOAA's aurora forecast, and sunlight from the Sun's actual direction; plus a flat-map view with the day/night line
- **Space theme**: twinkling starfield background, and a soft glow around each day's picture that takes on that picture's colors
- **Archive**: pick any past date to see that day's digest
- **Self-hosted image archive**: each day's picture is copied into our own storage, so the archive keeps working even when NASA changes its URLs (as it did when APOD moved from apod.nasa.gov to science.nasa.gov/apod)

## Architecture

```mermaid
flowchart LR
    NASA[NASA APOD API] --> F
    NOAA[NOAA SWPC feeds] --> F
    F["Azure Function<br/>(timer, daily 10:00 UTC)"] -->|"digests/YYYY-MM-DD.json<br/>images/YYYY-MM-DD.jpg"| B[(Azure Blob Storage)]
    B --> W[ASP.NET Core MVC site<br/>Azure App Service]
    W --> U((Visitors))
```

- `src/TodayInSpace.Function` — .NET 8 isolated-worker Azure Function. Fetches APOD, current Kp, solar wind, and the Kp forecast, then writes a dated JSON digest plus `latest.json` to blob storage. Non-critical feeds degrade gracefully (the digest still publishes without them).
- `src/TodayInSpace.Web` — .NET 10 ASP.NET Core MVC site. Reads digests from blob storage and serves archived images at `/images/{date}.{ext}` (storage stays private; responses are cacheable for a year since a day's image never changes). No database, no accounts.
- **Live Sky** — `/api/sky/tle/{iss|hubble}` and `/api/sky/aurora` fetch CelesTrak orbital elements (whitelisted satellites only) and NOAA's OVATION aurora model server-side, cache them (6 h / 10 min, serving the last good copy if an upstream is down), and trim the aurora grid to meaningful points. The browser propagates the ISS orbit itself with [satellite.js](https://github.com/shashwatak/satellite-js) (SGP4), so the marker moves every second with no per-visitor API calls. The globe uses [globe.gl](https://github.com/vasturiano/globe.gl) (three.js) with NASA Blue Marble imagery and a directional light placed at the subsolar point; the flat map uses [Leaflet](https://leafletjs.com/) with CARTO dark tiles; the day/night terminator is computed from a low-precision solar position formula.
- `src/TodayInSpace.Core` — shared logic used by both apps (image naming/validation).
- `tests/TodayInSpace.Web.Tests` — xUnit tests for the space-weather classification and image naming/fallback logic.

### Image archive backfill

`BackfillImages` is an HTTP-triggered function (protected by a function key) that copies images for digests saved before archiving existed. It works in batches and is safe to re-run:

```
GET https://<function-app>.azurewebsites.net/api/backfill-images?code=<function key>&max=20
```

Repeat until the response shows `"remaining": 0`. Days whose image can no longer be downloaded are listed under `failed` and keep NASA's link.

Decoupling ingestion (Function) from display (web app) means the site never calls NASA or NOAA on a page load: pages stay fast, API rate limits don't matter, and every day's data is preserved for the archive.

## Running locally

Requirements: .NET 10 SDK (and .NET 8 SDK + Azure Functions Core Tools for the function).

```bash
# point the web app at a storage account (kept out of the repo via user-secrets)
cd src/TodayInSpace.Web
dotnet user-secrets set "Storage:ConnectionString" "<your storage connection string>"
dotnet run
```

For fully offline development you can use [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite) with the connection string `UseDevelopmentStorage=true`.

The function reads `NASA_API_KEY`, `Storage__ConnectionString`, `Storage__ContainerName`, and optionally `Storage__ImagesContainerName` (default `images`) from environment variables / `local.settings.json` (git-ignored).

Run the tests:

```bash
dotnet test TodayInSpace.slnx
```

## Deploying

GitHub Actions (`.github/workflows/ci-cd.yml`) builds and tests every push and pull request. Pushes to `main` also deploy once these are configured in the repo settings:

| Type | Name | Value |
|---|---|---|
| Variable | `AZURE_WEBAPP_NAME` | App Service name |
| Secret | `AZURE_WEBAPP_PUBLISH_PROFILE` | Publish profile XML from the App Service |
| Variable | `AZURE_FUNCTIONAPP_NAME` | Function App name (optional) |
| Secret | `AZURE_FUNCTIONAPP_PUBLISH_PROFILE` | Publish profile XML from the Function App |

App settings in Azure: `Storage__ConnectionString` and `Storage__ContainerName` on both apps, plus `NASA_API_KEY` on the function.

## Background

Started as a team final project for CS 350 at South Puget Sound Community College (spring 2026). This repository is my continued version: the class-era email sign-up gate and the two-region client-side traffic split have been removed so the site is open to anyone and runs from a single region.

## Data sources

- [NASA APOD API](https://api.nasa.gov/)
- [NOAA Space Weather Prediction Center](https://www.swpc.noaa.gov/) (Kp, solar wind, OVATION aurora forecast)
- [CelesTrak](https://celestrak.org/) (ISS orbital elements)
- Map tiles &copy; [OpenStreetMap](https://www.openstreetmap.org/copyright) contributors &copy; [CARTO](https://carto.com/attributions)
