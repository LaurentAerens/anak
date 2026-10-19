"""
Automated Downloader for TCEC (Top Chess Engine Championship) Game Archives.
Supports downloading full seasons or specific divisions directly from tcec-chess.com
via URL or CLI arguments, and unzips the PGN files into training/data/.
"""

import argparse
import json
import os
import re
import urllib.parse
import urllib.request
import zipfile
from tqdm import tqdm

TCEC_BASE_URL = "https://tcec-chess.com"
GAMELIST_URL = f"{TCEC_BASE_URL}/gamelist.json"
ARCHIVE_BASE_URL = f"{TCEC_BASE_URL}/archive/json"


class DownloadProgressBar(tqdm):
    """Custom progress bar hook for urllib."""
    def update_to(self, b=1, bsize=1, tsize=None):
        if tsize is not None:
            self.total = tsize
        self.update(b * bsize - self.n)


def parse_tcec_url(url: str):
    """
    Parses season and division from a TCEC URL like:
    https://tcec-chess.com/#div=all&game=1&season=25
    """
    parsed = urllib.parse.urlparse(url)
    fragment = parsed.fragment
    params = urllib.parse.parse_qs(fragment)

    season = params.get("season", [None])[0]
    division = params.get("div", [None])[0]

    return season, division


def get_gamelist_data():
    """Fetches the official TCEC season and tournament index."""
    req = urllib.request.Request(GAMELIST_URL, headers={"User-Agent": "Mozilla/5.0"})
    with urllib.request.urlopen(req) as resp:
        return json.loads(resp.read().decode("utf-8"))


def download_file(url: str, output_path: str):
    """Downloads a file with an interactive progress bar."""
    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
    print(f"Downloading: {url}")
    print(f"Target:      {output_path}")

    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"})
    with urllib.request.urlopen(req, timeout=20) as resp:
        total_size = int(resp.headers.get("content-length", 0))

        with DownloadProgressBar(unit="B", unit_scale=True, miniters=1, desc=os.path.basename(output_path), total=total_size) as t:
            with open(output_path, "wb") as out_f:
                while True:
                    chunk = resp.read(65536)
                    if not chunk:
                        break
                    out_f.write(chunk)
                    t.update(len(chunk))

    print(f"Downloaded successfully: {output_path} ({os.path.getsize(output_path)} bytes)")


def extract_zip(zip_path: str, extract_dir: str):
    """Unzips archive and returns list of extracted files."""
    os.makedirs(extract_dir, exist_ok=True)
    print(f"Extracting {zip_path} into {extract_dir}...")
    extracted_files = []

    with zipfile.ZipFile(zip_path, "r") as zip_ref:
        for member in zip_ref.namelist():
            zip_ref.extract(member, extract_dir)
            extracted_path = os.path.join(extract_dir, member)
            extracted_files.append(extracted_path)
            print(f"  Extracted: {member} ({os.path.getsize(extracted_path)} bytes)")

    return extracted_files


def download_tcec_season(season: str, division: str = "all", output_dir: str = "./data"):
    """
    Resolves and downloads the requested season and division from TCEC.
    """
    os.makedirs(output_dir, exist_ok=True)

    print(f"Fetching TCEC index from {GAMELIST_URL}...")
    gamelist = get_gamelist_data()
    seasons = gamelist.get("Seasons", {})

    if season not in seasons:
        # Check if season name exists without prefix
        clean_season = season.lower().replace("season", "").replace("s", "").strip()
        if clean_season in seasons:
            season = clean_season
        else:
            raise ValueError(f"Season '{season}' not found in TCEC index. Available: {sorted(list(seasons.keys()))}")

    season_data = seasons[season]
    division_lower = (division or "all").lower()

    target_zip = None

    if division_lower == "all" and season_data.get("download"):
        target_zip = season_data["download"]
    else:
        # Look for division in sub events
        subs = season_data.get("sub", [])
        for sub in subs:
            sub_url = sub.get("url", "").lower()
            sub_abb = sub.get("abb", "")
            sub_menu = sub.get("menu", "").lower()

            if division_lower in sub_url or division_lower in sub_menu or division_lower in sub_abb.lower():
                target_zip = f"{sub_abb}.pgn.zip"
                break

        # Fallback to full season download if specific division name not found in sub
        if not target_zip and season_data.get("download"):
            print(f"Note: Specific division '{division}' not directly isolated; falling back to full season archive.")
            target_zip = season_data["download"]

    if not target_zip:
        raise RuntimeError(f"Could not determine download archive for Season {season}, division {division}")

    # Build download URL
    download_url = f"{ARCHIVE_BASE_URL}/{target_zip}"
    local_zip_path = os.path.join(output_dir, target_zip)

    # 1. Download
    download_file(download_url, local_zip_path)

    # 2. Extract
    extracted = extract_zip(local_zip_path, output_dir)

    print("\n=== Download & Extraction Complete ===")
    for f in extracted:
        if f.endswith(".pgn"):
            print(f"Ready for NNUE Training: {os.path.abspath(f)}")

    return extracted


def download_season(season: int = 25, output_dir: str = "./data"):
    """Alias for download_tcec_season with all divisions."""
    return download_tcec_season(season=str(season), division="all", output_dir=output_dir)


def download_by_division(season: int = 25, division: str = "premier", output_dir: str = "./data"):
    """Alias for download_tcec_season with specific division."""
    return download_tcec_season(season=str(season), division=division, output_dir=output_dir)


def main():
    parser = argparse.ArgumentParser(description="Automated downloader for TCEC Season PGN archives")
    parser.add_argument("--url", type=str, default=None,
                        help="TCEC web URL (e.g. 'https://tcec-chess.com/#div=all&game=1&season=25')")
    parser.add_argument("--season", type=str, default="25",
                        help="TCEC Season number (default: 25)")
    parser.add_argument("--division", type=str, default="all",
                        help="Division name (default: all, or 'sf', 'p', 'l1', etc.)")
    parser.add_argument("--output", type=str, default="./data",
                        help="Output directory (default: ./data)")

    args = parser.parse_args()

    season = args.season
    division = args.division

    if args.url:
        parsed_season, parsed_div = parse_tcec_url(args.url)
        if parsed_season:
            season = parsed_season
        if parsed_div:
            division = parsed_div
        print(f"Parsed from URL -> Season: {season}, Division: {division}")

    download_tcec_season(season=season, division=division, output_dir=args.output)


if __name__ == "__main__":
    main()
