using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TodayInSpace.Core.Visibility;
using TodayInSpace.Web.Controllers;
using TodayInSpace.Web.Sky;

namespace TodayInSpace.Web.Tests
{
    public class ZipLocationsTests
    {
        // The same format tools/build-zip-data.py writes.
        private const string Csv = "zip,lat,lon\n00601,18.181,-66.750\n98501,46.979,-122.876\n98502,47.093,-123.024\r\nbad line\n";

        private static LocationController Controller(string csv = Csv) => new(new ZipLookup(csv))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        [Fact]
        public void Parse_KeepsLeadingZeros_AndSkipsHeaderAndBadLines()
        {
            var zips = ZipLocations.Parse(Csv);

            Assert.Equal(3, zips.Count);
            Assert.Equal(new ZipLocation("00601", 18.181, -66.750, false), zips.Find("00601"));
        }

        [Fact]
        public void Find_ExactZip()
        {
            Assert.Equal(new ZipLocation("98501", 46.979, -122.876, false), ZipLocations.Parse(Csv).Find("98501"));
        }

        [Fact]
        public void Find_UnknownZip_FallsBackToItsThreeDigitArea()
        {
            // 98599 is a PO-box zip with no Census area: use the average of the known 985xx zips.
            var location = ZipLocations.Parse(Csv).Find("98599")!;

            Assert.True(location.Approximate);
            Assert.Equal(47.036, location.Lat);
            Assert.Equal(-122.950, location.Lon);
        }

        [Fact]
        public void Find_NothingNearby_IsNull()
        {
            Assert.Null(ZipLocations.Parse(Csv).Find("20500"));
        }

        [Theory]
        [InlineData("9850")]
        [InlineData("985011")]
        [InlineData("98a01")]
        [InlineData(" 98501")]
        public void Endpoint_RejectsAnythingButFiveDigits(string zip)
        {
            Assert.IsType<BadRequestResult>(Controller().GetZip(zip));
        }

        [Fact]
        public void Endpoint_UnknownZip_Is404()
        {
            Assert.IsType<NotFoundResult>(Controller().GetZip("20500"));
        }

        [Fact]
        public void Endpoint_KnownZip_IsCachedForADay()
        {
            var controller = Controller();

            var ok = Assert.IsType<OkObjectResult>(controller.GetZip("98501"));
            Assert.Equal("98501", ((ZipLocation)ok.Value!).Zip);
            Assert.Equal("public, max-age=86400", controller.Response.Headers.CacheControl.ToString());
        }

        [Fact]
        public void RealDataFile_HasTheCensusSpotChecks()
        {
            var zips = new ZipLookup().Zips;

            Assert.True(zips.Count > 30000);
            Assert.Equal(new ZipLocation("98501", 46.979, -122.876, false), zips.Find("98501"));
            Assert.Equal(new ZipLocation("10001", 40.751, -73.997, false), zips.Find("10001"));
            Assert.Equal(new ZipLocation("00601", 18.181, -66.750, false), zips.Find("00601"));
            Assert.True(zips.Find("98599")!.Approximate);
        }
    }
}
