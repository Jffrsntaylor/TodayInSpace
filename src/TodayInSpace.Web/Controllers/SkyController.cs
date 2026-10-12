using Microsoft.AspNetCore.Mvc;
using TodayInSpace.Core.Snapshot;
using TodayInSpace.Core.Visibility;
using TodayInSpace.Web.Sky;

namespace TodayInSpace.Web.Controllers
{
    // JSON endpoints behind the Live Sky map on the homepage.
    [ApiController]
    [Route("api/sky")]
    public class SkyController : ControllerBase
    {
        private readonly SkyDataService _sky;

        public SkyController(SkyDataService sky)
        {
            _sky = sky;
        }

        // GET /api/sky/tle/iss, /api/sky/tle/hubble, /api/sky/tle/tiangong
        [HttpGet("tle/{id}")]
        public async Task<IActionResult> GetTle(string id)
        {
            if (!SkyDataService.Satellites.ContainsKey(id))
                return NotFound();

            var tle = await _sky.GetTleAsync(id);
            if (tle == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable);

            Response.Headers.CacheControl = "public, max-age=3600";
            return Ok(tle);
        }

        // GET /api/sky/visible?lat=47.04&lon=-122.90
        // Visible ISS, Tiangong and Hubble passes for the next 7 days. Takes lat/lon rather than a zip so an app
        // with GPS can call it directly. The service is injected here so the other endpoints don't build it.
        [HttpGet("visible")]
        public async Task<IActionResult> GetVisible([FromQuery] double? lat, [FromQuery] double? lon, [FromServices] VisibleSkyService visible)
        {
            if (!SkyLocation.IsValid(lat, lon))
                return BadRequest();

            var sky = await visible.GetAsync(lat!.Value, lon!.Value);

            Response.Headers.CacheControl = "public, max-age=900";
            return Ok(sky);
        }

        // GET /api/sky/earth.png?lat=47.04&lon=-122.90&size=400   (all optional; also center=iss, format=card)
        // A picture of the Earth right now: day and night, aurora, the ISS and its track, and a pin for the place.
        // Without a place it's centred on the ISS. Made for home-screen widgets, which refresh a static image.
        [HttpGet("earth.png")]
        public async Task<IActionResult> GetEarth([FromQuery] double? lat, [FromQuery] double? lon, [FromQuery] string? center,
            [FromQuery] int? size, [FromQuery] string? format, [FromServices] EarthSnapshotService earth)
        {
            var request = EarthSnapshotRequest.Parse(lat, lon, center, size, format);
            if (request == null)
                return BadRequest();

            var png = await earth.GetPngAsync(request);
            if (png == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable);

            // Matches the server's 10-minute picture, so widgets and link-preview crawlers don't ask more often.
            Response.Headers.CacheControl = "public, max-age=600";
            return File(png, "image/png");
        }

        // GET /api/sky/aurora
        [HttpGet("aurora")]
        public async Task<IActionResult> GetAurora()
        {
            var aurora = await _sky.GetAuroraAsync();
            if (aurora == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable);

            Response.Headers.CacheControl = "public, max-age=300";
            return Ok(aurora);
        }

        // GET /api/sky/people
        [HttpGet("people")]
        public async Task<IActionResult> GetPeople()
        {
            var people = await _sky.GetPeopleAsync();
            if (people == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable);

            // Short, so browsers and home-screen installs pick up the 3-hourly refresh.
            Response.Headers.CacheControl = "public, max-age=600";
            return Ok(people);
        }

        // GET /api/sky/launches
        [HttpGet("launches")]
        public async Task<IActionResult> GetLaunches()
        {
            var launches = await _sky.GetLaunchesAsync();
            if (launches == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable);

            // Short, because the next launch is refreshed hourly and its time can slip.
            Response.Headers.CacheControl = "public, max-age=600";
            return Ok(launches);
        }

        // GET /api/sky/satellites
        [HttpGet("satellites")]
        public async Task<IActionResult> GetSatellites()
        {
            var satellites = await _sky.GetSatellitesAsync();
            if (satellites == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable);

            // An hour is fine: the catalogue changes a few times a day at most.
            Response.Headers.CacheControl = "public, max-age=3600";
            return Ok(satellites);
        }

        // GET /api/sky/active-satellites
        // Every active satellite's TLE for the globe's point cloud. Only fetched when a visitor turns on a group,
        // and served exactly as the function stored it (compressed on the way out, see Program.cs).
        [HttpGet("active-satellites")]
        public async Task<IActionResult> GetActiveSatellites([FromServices] ActiveSatellitesService active)
        {
            var json = await active.GetJsonAsync();
            if (json == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable);

            // The function refreshes every 6 hours, and a few hours' old TLEs are still accurate to a few km.
            Response.Headers.CacheControl = "public, max-age=3600";
            return Content(json, "application/json");
        }
    }
}
