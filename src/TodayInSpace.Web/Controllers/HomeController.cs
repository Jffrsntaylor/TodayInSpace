using Microsoft.AspNetCore.Mvc;
using TodayInSpace.Core;
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

        // Archive: shows a date picker and the digest for ?date=2026-06-02.
        public async Task<IActionResult> Archive(string? date)
        {
            var todayUtc = DateOnly.FromDateTime(DateTime.UtcNow);

            if (string.IsNullOrWhiteSpace(date))
            {
                // No date: send people to yesterday so the normal date path fills in the picker
                // and arrows, and the URL can be bookmarked. Not a 301, because browsers cache
                // those and "yesterday" changes every day.
                return Redirect($"/Home/Archive?date={ArchiveNav.DefaultDate(todayUtc)}");
            }

            // Pass the selected date back to the view so the picker stays on it.
            ViewBag.SelectedDate = date;

            // Previous/next day links. Set before the lookup so they still show on
            // "No forecast found", letting people step past a missing day.
            var (prevDate, nextDate) = ArchiveNav.GetNeighbors(date, todayUtc);
            ViewBag.PrevDate = prevDate;
            ViewBag.NextDate = nextDate;

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