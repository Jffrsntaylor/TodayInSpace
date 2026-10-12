"""Rebuilds src/TodayInSpace.Web/Data/zcta-2025.csv (zip -> lat, lon, city, state).

Locations come from the Census ZCTA Gazetteer (public domain, U.S. Census Bureau):
https://www2.census.gov/geo/docs/maps-data/data/gazetteer/2025_Gazetteer/2025_Gaz_zcta_national.zip

City and state are the zip's postal name, from GeoNames' US postal codes (CC BY 4.0, credited on the page):
https://download.geonames.org/export/zip/US.zip
The Census file has no names, and the weather service names the town nearest its forecast grid point,
which for 98501 is Tumwater rather than Olympia.

Usage:  python tools/build-zip-data.py [path/to/2025_Gaz_zcta_national.zip [path/to/geonames/US.zip]]
Any file not given (or given as "") is downloaded. Only the standard library is used.
"""

import io
import sys
import urllib.request
import zipfile
from pathlib import Path

SOURCE_URL = "https://www2.census.gov/geo/docs/maps-data/data/gazetteer/2025_Gazetteer/2025_Gaz_zcta_national.zip"
NAMES_URL = "https://download.geonames.org/export/zip/US.zip"
OUTPUT = Path(__file__).resolve().parent.parent / "src" / "TodayInSpace.Web" / "Data" / "zcta-2025.csv"

# Spot checks from the Census file itself, so a format change (the 2025 file switched from tabs to pipes) fails loudly.
CHECKS = {"98501": (46.979, -122.876), "10001": (40.751, -73.997), "00601": (18.181, -66.750)}
NAME_CHECKS = {"98501": ("Olympia", "WA"), "10001": ("New York", "NY"), "99701": ("Fairbanks", "AK")}


def read_gazetteer(zip_bytes):
    with zipfile.ZipFile(io.BytesIO(zip_bytes)) as archive:
        name = next(n for n in archive.namelist() if n.endswith(".txt"))
        text = archive.read(name).decode("utf-8-sig")

    lines = text.splitlines()
    header = [h.strip() for h in lines[0].split("|")]
    zip_col, lat_col, lon_col = header.index("GEOID"), header.index("INTPTLAT"), header.index("INTPTLONG")

    rows = []
    for line in lines[1:]:
        fields = [f.strip() for f in line.split("|")]
        if len(fields) < len(header):
            continue
        # Keep the zip as text: Puerto Rico and New England zips start with 0.
        rows.append((fields[zip_col], float(fields[lat_col]), float(fields[lon_col])))
    return rows


def read_names(zip_bytes):
    """GeoNames US.txt is tab-separated: country, zip, place name, state name, state code, ..."""
    with zipfile.ZipFile(io.BytesIO(zip_bytes)) as archive:
        text = archive.read("US.txt").decode("utf-8")

    names = {}
    for line in text.splitlines():
        fields = line.split("\t")
        if len(fields) < 5:
            continue
        zip_code, city, state = fields[1].strip(), fields[2].strip(), fields[4].strip()
        # Military APO/FPO zips have no state, and the output is comma-separated.
        if len(state) != 2 or not city or "," in city:
            continue
        names[zip_code] = (city, state)
    return names


def load(index, url):
    if len(sys.argv) > index and sys.argv[index]:
        return Path(sys.argv[index]).read_bytes()
    print(f"Downloading {url}")
    with urllib.request.urlopen(url) as response:
        return response.read()


def main():
    rows = sorted(read_gazetteer(load(1, SOURCE_URL)))
    found = {z: (round(lat, 3), round(lon, 3)) for z, lat, lon in rows}
    for z, expected in CHECKS.items():
        if found.get(z) != expected:
            sys.exit(f"Check failed for {z}: got {found.get(z)}, expected {expected}")

    names = read_names(load(2, NAMES_URL))
    for z, expected in NAME_CHECKS.items():
        if names.get(z) != expected:
            sys.exit(f"Name check failed for {z}: got {names.get(z)}, expected {expected}")

    # 3 decimals is about 100 m, far finer than a zip code's size.
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    with OUTPUT.open("w", encoding="utf-8", newline="\n") as out:
        out.write("zip,lat,lon,city,state\n")
        for z, lat, lon in rows:
            # Empty when GeoNames has no name for it (Puerto Rico's zips are in a separate file, for one).
            city, state = names.get(z, ("", ""))
            out.write(f"{z},{lat:.3f},{lon:.3f},{city},{state}\n")
    named = sum(1 for z, _, _ in rows if z in names)
    print(f"Wrote {len(rows)} zips ({named} with names) to {OUTPUT}")


if __name__ == "__main__":
    main()
