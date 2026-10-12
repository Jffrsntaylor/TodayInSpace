"""Rebuilds src/TodayInSpace.Web/Data/zcta-2025.csv (zip -> lat, lon) from the Census ZCTA Gazetteer.

Source (public domain, U.S. Census Bureau):
https://www2.census.gov/geo/docs/maps-data/data/gazetteer/2025_Gazetteer/2025_Gaz_zcta_national.zip

Usage:  python tools/build-zip-data.py [path/to/2025_Gaz_zcta_national.zip]
With no argument it downloads the file. Only the standard library is used.
"""

import io
import sys
import urllib.request
import zipfile
from pathlib import Path

SOURCE_URL = "https://www2.census.gov/geo/docs/maps-data/data/gazetteer/2025_Gazetteer/2025_Gaz_zcta_national.zip"
OUTPUT = Path(__file__).resolve().parent.parent / "src" / "TodayInSpace.Web" / "Data" / "zcta-2025.csv"

# Spot checks from the Census file itself, so a format change (the 2025 file switched from tabs to pipes) fails loudly.
CHECKS = {"98501": (46.979, -122.876), "10001": (40.751, -73.997), "00601": (18.181, -66.750)}


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


def main():
    if len(sys.argv) > 1:
        zip_bytes = Path(sys.argv[1]).read_bytes()
    else:
        print(f"Downloading {SOURCE_URL}")
        with urllib.request.urlopen(SOURCE_URL) as response:
            zip_bytes = response.read()

    rows = sorted(read_gazetteer(zip_bytes))
    found = {z: (round(lat, 3), round(lon, 3)) for z, lat, lon in rows}
    for z, expected in CHECKS.items():
        if found.get(z) != expected:
            sys.exit(f"Check failed for {z}: got {found.get(z)}, expected {expected}")

    # 3 decimals is about 100 m, far finer than a zip code's size.
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    with OUTPUT.open("w", encoding="utf-8", newline="\n") as out:
        out.write("zip,lat,lon\n")
        for z, lat, lon in rows:
            out.write(f"{z},{lat:.3f},{lon:.3f}\n")
    print(f"Wrote {len(rows)} zips to {OUTPUT}")


if __name__ == "__main__":
    main()
