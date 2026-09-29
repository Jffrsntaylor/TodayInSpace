using Microsoft.AspNetCore.Mvc;
using TodayInSpace.Web.Models;
using TodayInSpace.Web.Services;
using System.Diagnostics;

namespace TodayInSpace.Web.Controllers
{
    public class HomeController : Controller
    {
        private readonly DigestService _digestService;

        public HomeController(DigestService digestService)
        {
            _digestService = digestService;
        }

        // Homepage: shows the latest digest.
        public async Task<IActionResult> Index()
        {
            var digest = await _digestService.GetLatestAsync();
            return View(digest);
        }

        // Archive: shows a date picker. If a date is provided (?date=2026-06-02),
        // it loads that day's digest; otherwise it just shows the picker.
        public async Task<IActionResult> Archive(string? date)
        {
            // Pass the selected date back to the view so the picker stays on it.
            ViewBag.SelectedDate = date;

            if (string.IsNullOrWhiteSpace(date))
            {
                // No date chosen yet — show the picker with no digest loaded.
                ViewBag.NoDateChosen = true;
                return View(null);
            }

            var digest = await _digestService.GetByDateAsync(date);

            // digest will be null if there's no forecast stored for that day —
            // the view handles that with a friendly message.
            ViewBag.NotFound = (digest == null);
            return View(digest);
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}