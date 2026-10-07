using TodayInSpace.Web.Helpers;

namespace TodayInSpace.Web.Tests
{
    public class FlagIconsTests
    {
        private static readonly IReadOnlySet<string> Available = FlagIcons.ListCodes(new[] { "us.svg", "cn.svg", "README.md" });

        [Fact]
        public void ListCodes_KeepsOnlySvgs()
        {
            Assert.Equal(new HashSet<string> { "us", "cn" }, Available);
        }

        [Fact]
        public void Src_KnownCode_IsTheShippedFile()
        {
            Assert.Equal("/lib/flag-icons/4x3/us.svg", FlagIcons.Src("US", Available));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("us")]      // LL2 sends uppercase codes, same rule as the home page
        [InlineData("USA")]
        [InlineData("../x")]
        [InlineData("JP")]      // a real code, but no file for it
        public void Src_InvalidOrMissing_IsNull(string? code)
        {
            Assert.Null(FlagIcons.Src(code, Available));
        }
    }
}
