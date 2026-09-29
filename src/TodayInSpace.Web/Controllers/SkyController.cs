using Microsoft.AspNetCore.Mvc;
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

        // GET /api/sky/iss-tle
        [HttpGet("iss-tle")]
        public async Task<IActionResult> GetIssTle()
        {
            var tle = await _sky.GetIssTleAsync();
            if (tle == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable);

            Response.Headers.CacheControl = "public, max-age=3600";
            return Ok(tle);
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
    }
}
