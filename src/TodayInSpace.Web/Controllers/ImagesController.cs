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
        // An archived day's image never changes, so browsers and CDNs can cache it for a year.
        [ResponseCache(Duration = 31536000, Location = ResponseCacheLocation.Any)]
        public async Task<IActionResult> Get(string name)
        {
            var image = await _digestService.OpenImageAsync(name);
            if (image == null)
                return NotFound();

            return File(image.Value.Content, image.Value.ContentType);
        }
    }
}
