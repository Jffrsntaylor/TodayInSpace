using Microsoft.AspNetCore.Mvc;
using TodayInSpace.Web.Services;

namespace TodayInSpace.Web.Controllers
{
    // Serves archived APOD images from private blob storage, e.g. /images/2026-09-28.jpg
    public class ImagesController : Controller
    {
        private readonly DigestService _digestService;

        public ImagesController(DigestService digestService)
        {
            _digestService = digestService;
        }

        [HttpGet("/images/{name}")]
        public async Task<IActionResult> Get(string name)
        {
            var image = await _digestService.OpenImageAsync(name);
            if (image == null)
                return NotFound();

            // Cache for a day: long enough to be fast, short enough that a corrected image
            // (e.g. after NASA sent a placeholder) shows up. Set only on success so a 404 isn't cached.
            Response.Headers.CacheControl = "public, max-age=86400";
            return File(image.Value.Content, image.Value.ContentType);
        }
    }
}
