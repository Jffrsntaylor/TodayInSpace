# Today in Space

Public portfolio site by Jeff Taylor: NASA's Astronomy Picture of the Day, NOAA space weather,
and a live 3D globe tracking the ISS and Hubble. Live: https://tis-app-east-hqdhdkh5eqfwh2ee.eastus-01.azurewebsites.net/

Recruiters read this repo. Keep code readable, commits clean, and the README simple and in Jeff's voice.

## How we work

- **Jeff** owns the project and decides when each PR merges. Claude Code may merge a PR only when Jeff
  asks in chat, and only after CI is green. A merge to `main` deploys, so never merge on your own initiative.
- **Claude in Cowork** is the lead engineer. It plans the work, writes task briefs, and reviews PRs.
- **You (Claude Code)** are the engineer who builds. Briefs live in `.claude/briefs/<name>.md` (gitignored).
  Run `/build-brief <name>` to do one end to end. For small asks, Jeff may just tell you directly.
- Stay inside the brief's scope. If something outside it looks wrong, mention it in the PR body under
  "Noticed, not changed". Don't fix it.

## Layout

```
TodayInSpace.slnx
src/TodayInSpace.Core/       net10.0 library. Pure logic, no I/O: APOD parsing/validation (ApodSources),
                             video embeds (ApodVideo), image naming, NOAA parsers. Put testable logic here.
src/TodayInSpace.Function/   Azure Functions isolated worker (net10.0)
  FetchDailyDigest.cs        timer 10:00 + 18:00 UTC. APOD: api.nasa.gov -> NASA RSS feed -> last good day.
                             Writes digests/YYYY-MM-DD.json + latest.json and archives the image to images/.
  BackfillImages.cs          HTTP, function-key protected
src/TodayInSpace.Web/        ASP.NET Core MVC (net10.0)
  Controllers/               Home (Index, Archive?date=), Images (/images/{name}), Sky (api/sky/tle/{id}, api/sky/aurora)
  Services/DigestService.cs  reads blobs. Only YYYY-MM-DD names are accepted.
  Sky/                       TLE + OVATION parsing, cached fetch (IMemoryCache, stale fallback)
  Helpers/                   view helpers (ApodImageHelper, SpaceWeatherHelper)
  Views/Home/                Index + Archive are Layout = null and share Shared/_SocialMeta.cshtml
  wwwroot/js/                sky-core.js (window.TISSky shared state), sky-globe.js, sky-map.js, starfield.js
  wwwroot/lib/               vendored globe.gl, leaflet, satellite.js. Never edit these.
tests/TodayInSpace.Web.Tests xUnit, references Web + Core
```

## Commands

```
dotnet build TodayInSpace.slnx
dotnet test TodayInSpace.slnx
dotnet run --project src/TodayInSpace.Web     # needs: dotnet user-secrets set "Storage:ConnectionString" "..."
```

CI (.github/workflows/ci-cd.yml) builds and tests every PR. A merge to `main` deploys to Azure automatically.

## Rules

- Never commit or push to `main`. Always use a branch (`feature/…`, `fix/…`, `chore/…`) and open a PR.
- Never force-push. Never rewrite history that's already pushed.
- Build and test locally before pushing. Don't open a PR with failing tests.
- Add or update xUnit tests for new logic. Prefer putting logic in Core, where it's easy to test.
- Never read, print, or commit secrets. That covers connection strings, publish profiles, function keys,
  `local.settings.json`, and user-secrets. `appsettings.json` keeps `ConnectionString` empty.
- Don't run `az` or change anything in Azure or GitHub settings. Ask Jeff. Confirm with him before anything
  that costs money or deletes data. The storage blobs (every past day and image) are irreplaceable.
- No new NuGet/npm dependencies without saying why in the PR. Front-end libraries get vendored into `wwwroot/lib`
  with their license. No CDNs.
- Plain, readable C#. Explain the *why* in comments, not the what.

## Gotchas we've already hit

- api.nasa.gov sometimes returns a placeholder ("NASA Science" + the NASA logo) or a 500/503 error.
  `ApodSources.LooksLikeRealApod` guards against it, so don't loosen it.
- The NASA RSS feed has no media type. Video days are found from the article page's `og:video`.
- NOAA retired `plasma-1-day.json`. Solar wind now comes from `products/summary/solar-wind-speed.json`.
- `upload-artifact` needs `include-hidden-files: true`, or the function app deploys with 0 functions.
- Globe events: `TISSky.on` replays `ready` to late subscribers. Keep longitudes continuous with `unwrapNear`
  (antimeridian).
- `GetBlobsAsync` needs the explicit overload `GetBlobsAsync(BlobTraits.None, BlobStates.None, null, default)`.
