using Microsoft.AspNetCore.Mvc;
using TodayInSpace.Core.Visibility;
using TodayInSpace.Web.Sky;

namespace TodayInSpace.Web.Controllers
{
    // Turns a US zip code into a latitude/longitude for the sky API. It's a separate step on purpose:
    // a phone with GPS can call /api/sky/visible directly and never needs it.
    [ApiController]
    [Route("api/location")]
    public class LocationController : ControllerBase
    {
        private readonly ZipLookup _zips;

        public LocationController(ZipLookup zips)
        {
            _zips = zips;
        }

        // GET /api/location/zip/98501
        [HttpGet("zip/{zip}")]
        public IActionResult GetZip(string zip)
        {
            if (!ZipLocations.IsValidZip(zip))
                return BadRequest();

            var location = _zips.Zips.Find(zip);
            if (location == null)
                return NotFound();

            // Zip locations only change when the Census file is rebuilt.
            Response.Headers.CacheControl = "public, max-age=86400";
            return Ok(location);
        }
    }
}
