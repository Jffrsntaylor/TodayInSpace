using TodayInSpace.Core;

namespace TodayInSpace.Web.Tests
{
    // The counting rules for the Satellites card, run against a small handmade SATCAT file with the real header.
    public class SatellitesTests
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 11, 18, 30, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset CatalogUpdated = new(2026, 10, 11, 18, 52, 57, TimeSpan.Zero);

        private static readonly string Csv = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "satcat-sample.csv"));

        // The fixture has 17 rows, so the real 50,000-row floor is lowered to match.
        private static SatellitesSnapshot Sample() => Satcat.Build(Csv, CatalogUpdated, "\"abc\"", Now, minRows: 17)!;

        [Fact]
        public void Build_CountsWhatIsInEarthOrbit()
        {
            var s = Sample();

            // Leaves out the decayed Starlink, the module docked to the ISS and the solar probe.
            Assert.Equal(14, s.InOrbit);
            Assert.Equal(10, s.Payloads);
            Assert.Equal(1, s.Debris);
            Assert.Equal(2, s.RocketBodies);
            Assert.Equal(1, s.Unknown);
        }

        [Fact]
        public void Build_CountsActiveSatellites()
        {
            var s = Sample();

            // Not the dead Vanguard, and not the Falcon 9 stage marked "+": it's a rocket body, not a satellite.
            Assert.Equal(9, s.Active);
            // Matched without caring about case; the decayed one doesn't count.
            Assert.Equal(2, s.Starlink);
        }

        [Fact]
        public void Build_KeepsCatalogDetailsAndCheckedTime()
        {
            var s = Sample();

            Assert.Equal(CatalogUpdated, s.CatalogUpdated);
            Assert.Equal("\"abc\"", s.ETag);
            Assert.Equal(Now, s.Checked);
            Assert.Equal(Satcat.Source, s.Source);
        }

        [Fact]
        public void Build_GroupsOwners_BiggestFirst_UnassignedLast()
        {
            var owners = Sample().Owners;

            Assert.Equal(new[] { "US", "PRC", "JPN", "CIS", "UK", "XYZ", "TBD" }, owners.Select(o => o.Code));
            Assert.Equal(3, owners[0].Count);
            Assert.Equal(9, owners.Sum(o => o.Count));
        }

        [Fact]
        public void Build_NamesOwners()
        {
            var owners = Sample().Owners.ToDictionary(o => o.Code);

            Assert.Equal(new OwnerCount("CIS", "Russia", "RU", 1), owners["CIS"]);
            Assert.Equal(new OwnerCount("US", "United States", "US", 3), owners["US"]);
            Assert.Equal(new OwnerCount("TBD", "Unassigned", null, 1), owners["TBD"]);
            // An unknown code is shown as-is, with no flag.
            Assert.Equal(new OwnerCount("XYZ", "XYZ", null, 1), owners["XYZ"]);
        }

        [Fact]
        public void Owner_Organization_HasNoFlag_ExceptEsa()
        {
            Assert.Null(Satcat.Owner("SES", 1).Flag);
            Assert.Equal("EU", Satcat.Owner("ESA", 1).Flag);
        }

        [Fact]
        public void Build_TooFewRows_IsNull()
        {
            Assert.Null(Satcat.Build(Csv, CatalogUpdated, null, Now, minRows: 18));
            Assert.Null(Satcat.Build(Csv, CatalogUpdated, null, Now));
        }

        [Fact]
        public void Build_MissingHeader_IsNull()
        {
            var csv = Csv.Replace("ORBIT_CENTER", "CENTER");

            Assert.Null(Satcat.Build(csv, CatalogUpdated, null, Now, minRows: 1));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("<html>Service unavailable</html>")]
        public void Build_NotACsv_IsNull(string? csv)
        {
            Assert.Null(Satcat.Build(csv, CatalogUpdated, null, Now, minRows: 0));
        }

        [Fact]
        public void Build_ReadsColumnsByName()
        {
            // Same data with the first and last columns swapped.
            var lines = SatellitesCsv.Rows(Csv)
                .Select(f => { (f[0], f[16]) = (f[16], f[0]); return string.Join(",", f.Select(Quote)); });

            var s = Satcat.Build(string.Join("\n", lines), CatalogUpdated, null, Now, minRows: 17)!;

            Assert.Equal((9, 2, 14), (s.Active, s.Starlink, s.InOrbit));
        }

        private static string Quote(string field) => field.Contains(',') ? "\"" + field + "\"" : field;

        [Fact]
        public void Csv_HandlesQuotedFields()
        {
            var rows = SatellitesCsv.Rows("a,\"b, c\",\"say \"\"hi\"\"\"\r\n\"two\nlines\",x\r\n\r\n").ToList();

            Assert.Equal(2, rows.Count);
            Assert.Equal(new[] { "a", "b, c", "say \"hi\"" }, rows[0]);
            Assert.Equal(new[] { "two\nlines", "x" }, rows[1]);
        }

        [Fact]
        public void Csv_KeepsEmptyFields()
        {
            Assert.Equal(new[] { "a", "", "", "b", "" }, SatellitesCsv.Rows("a,,,b,").Single());
        }

        [Theory]
        [InlineData(1000, 10000, 1250, 12500, false)]  // exactly 25% is fine
        [InlineData(1000, 10000, 750, 10000, false)]
        [InlineData(1000, 10000, 1251, 10000, true)]
        [InlineData(1000, 10000, 749, 10000, true)]
        [InlineData(1000, 10000, 1000, 12501, true)]
        [InlineData(1000, 10000, 1000, 7499, true)]
        public void LooksWrong_FlagsBigJumps(int prevActive, int prevInOrbit, int active, int inOrbit, bool expected)
        {
            var previous = Sample() with { Active = prevActive, InOrbit = prevInOrbit };
            var fresh = Sample() with { Active = active, InOrbit = inOrbit };

            Assert.Equal(expected, Satcat.LooksWrong(previous, fresh));
        }

        [Fact]
        public void LooksWrong_NothingToCompare_IsFalse()
        {
            Assert.False(Satcat.LooksWrong(null, Sample()));
        }

        [Fact]
        public void SerializeAndParse_RoundTrip()
        {
            var s = Sample();

            var parsed = Satcat.Parse(Satcat.Serialize(s))!;

            Assert.Equal((s.Active, s.Starlink, s.InOrbit, s.Payloads, s.Debris, s.RocketBodies, s.Unknown),
                (parsed.Active, parsed.Starlink, parsed.InOrbit, parsed.Payloads, parsed.Debris, parsed.RocketBodies, parsed.Unknown));
            Assert.Equal(s.Owners, parsed.Owners);
            Assert.Equal((s.CatalogUpdated, s.ETag, s.Checked), (parsed.CatalogUpdated, parsed.ETag, parsed.Checked));
        }

        [Fact]
        public void Serialize_UsesTheNamesTheFrontEndReads()
        {
            var json = Satcat.Serialize(Sample());

            foreach (var name in new[] { "\"active\"", "\"starlink\"", "\"owners\"", "\"inOrbit\"", "\"payloads\"", "\"debris\"",
                                         "\"rocketBodies\"", "\"unknown\"", "\"catalogUpdated\"", "\"checked\"", "\"flag\"" })
                Assert.Contains(name, json);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("not json")]
        [InlineData("{}")]
        public void Parse_NotASnapshot_IsNull(string? json)
        {
            Assert.Null(Satcat.Parse(json));
        }

        [Fact]
        public void IsTooOld_AfterSevenDays()
        {
            var s = Sample();

            Assert.False(Satcat.IsTooOld(s, Now.AddDays(7)));
            Assert.True(Satcat.IsTooOld(s, Now.AddDays(7).AddMinutes(1)));
        }
    }
}
