#!/usr/bin/env python3
"""Builds the Partner Center Store listing import folders (and ZIPs) for Valafind.

Input:
  text/<language>.json   the listing texts, English (en.json) is the source; one file per UI language
  fields.csv             Partner Center's listing fields (Field, ID, Type), as in an exported listing CSV
  --images DIR           what StoreListingScreenshotTests rendered: <lang>/screenshots/*.png and logos/*.png

Output (in --out):
  valafind-store-listing-<n>-of-<parts>/  every language, split over a few folders (--parts, default 5) so each upload
                                          stays small: import them one after the other to update all languages
  valafind-store-listing-<lang>/          one language each, for updating a single listing
  and a .zip of each folder.

In Partner Center: the app > Store listings (any language) > Import listings > Upload folder, then pick the extracted
folder. Image paths in the CSV start with the folder name, as Partner Center expects.

The CSV's language columns are the languages the package declares (windows/AppxManifest.xml, <Resource Language>),
which become the submission's listing languages. If Partner Center still names a language differently, export the listing once (Export listings) and pass that CSV as
--columns-from: its language columns are then matched to ours by language (de-de -> de, zh-cn -> zh-Hans, ...).
"""
from __future__ import annotations

import argparse
import csv
import json
import re
import shutil
import sys
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent

MANIFEST = HERE.parent / "windows" / "AppxManifest.xml"


def manifest_locales() -> dict[str, str]:
    """UI language (text/<code>.json) -> Partner Center listing column: the language the package declares for it."""
    declared = re.findall(r'<Resource Language="([^"]+)"', MANIFEST.read_text(encoding="utf-8"))
    locales = {}
    for code in (p.stem for p in sorted((HERE / "text").glob("*.json"))):
        match = [d for d in declared if d.lower() == code.lower() or d.lower().startswith(code.lower() + "-")]
        if not match:
            sys.exit(f"{MANIFEST.name} declares no language for text/{code}.json")
        locales[code] = match[0].lower()
    return locales


# In the order of the app's language picker (Loc.Languages): English first.
ORDER = ["en", "zh-Hans", "hi", "es", "ar", "fr", "bn", "pt", "ru", "id", "ur", "de", "ja", "mr", "vi", "te", "ha", "tr",
         "ta", "ko", "da"]
_DECLARED = manifest_locales()
LOCALES = {code: _DECLARED[code] for code in ORDER if code in _DECLARED}

SCREENSHOTS = ["search", "media", "photos", "recordings", "indexing", "settings"]
LOGOS = {"StoreLogo720x1080": "poster-1440x2160.png", "StoreLogo1080x1080": "boxart-2160x2160.png",
         "StoreLogo300x300": "tile-300x300.png"}
COPYRIGHT = "© 2026 Valafind contributors"

# Partner Center's limits (characters), checked before anything is written.
LIMITS = {"Description": 10000, "ReleaseNotes": 1500, "ShortDescription": 1000, "Title": 256, "ShortTitle": 50,
          "SortTitle": 255, "VoiceTitle": 255, "AdditionalLicenseTerms": 10000, "CopyrightTrademarkInformation": 200}


def load_text(language: str) -> dict:
    return json.loads((HERE / "text" / f"{language}.json").read_text(encoding="utf-8"))


def check(language: str, text: dict) -> list[str]:
    problems = []
    for key, field in [("description", "Description"), ("releaseNotes", "ReleaseNotes"),
                       ("shortDescription", "ShortDescription"), ("title", "Title"), ("shortTitle", "ShortTitle"),
                       ("sortTitle", "SortTitle"), ("voiceTitle", "VoiceTitle"),
                       ("additionalLicenseTerms", "AdditionalLicenseTerms")]:
        if len(text[key]) > LIMITS[field]:
            problems.append(f"{language}: {key} is {len(text[key])} characters (limit {LIMITS[field]})")
    if len(text["features"]) > 20 or any(len(f) > 200 for f in text["features"]):
        problems.append(f"{language}: at most 20 features of 200 characters")
    terms = text["searchTerms"]
    if len(terms) > 7 or any(len(t) > 30 for t in terms) or sum(len(t.split()) for t in terms) > 21:
        problems.append(f"{language}: at most 7 search terms, 30 characters each, 21 words in all")
    for scene in SCREENSHOTS:
        if len(text["screenshots"][scene]["caption"]) > 200:
            problems.append(f"{language}: the {scene} caption is over 200 characters")
    return problems


def values(text: dict, images: str, logos: str) -> dict[str, str]:
    """Field name -> value for one language. images/logos: the folders as the CSV should name them."""
    v = {
        "Description": text["description"], "ReleaseNotes": text["releaseNotes"], "Title": text["title"],
        "ShortTitle": text["shortTitle"], "SortTitle": text["sortTitle"], "VoiceTitle": text["voiceTitle"],
        "ShortDescription": text["shortDescription"], "CopyrightTrademarkInformation": COPYRIGHT,
        "AdditionalLicenseTerms": text["additionalLicenseTerms"], "OverrideLogosForWin10": "False",
    }
    for i, scene in enumerate(SCREENSHOTS, 1):
        v[f"DesktopScreenshot{i}"] = f"{images}/{i:02d}-{scene}.png"
        v[f"DesktopScreenshotCaption{i}"] = text["screenshots"][scene]["caption"]
    for field, file in LOGOS.items():
        v[field] = f"{logos}/{file}"
    for i, feature in enumerate(text["features"], 1):
        v[f"Feature{i}"] = feature
    for i, term in enumerate(text["searchTerms"], 1):
        v[f"SearchTerm{i}"] = term
    return v


def write_csv(path: Path, columns: list[str], per_column: list[dict[str, str]]) -> None:
    fields = list(csv.reader((HERE / "fields.csv").open(encoding="utf-8")))[1:]
    known = {row[0] for row in fields}
    for v in per_column:
        unknown = set(v) - known
        if unknown:
            sys.exit(f"fields.csv has no {sorted(unknown)}")
    # Partner Center reads UTF-8 with a byte order mark; CRLF like its own exports.
    with path.open("w", encoding="utf-8-sig", newline="") as f:
        writer = csv.writer(f, lineterminator="\r\n")
        writer.writerow(["Field", "ID", "Type (Type)", "default", *columns])
        for name, field_id, kind in fields:
            writer.writerow([name, field_id, kind, "", *(v.get(name, "") for v in per_column)])


def columns_from(export: Path | None) -> dict[str, str]:
    if export is None:
        return dict(LOCALES)
    header = next(csv.reader(export.open(encoding="utf-8-sig")))
    exported = [c for c in header[4:] if c]
    mapped = {}
    for language in LOCALES:
        base = language.split("-")[0].lower()
        script = language.split("-")[1].lower() if "-" in language else None
        candidates = [c for c in exported if c.lower().split("-")[0] == base]
        if script == "hans":
            candidates = [c for c in candidates if c.lower() in ("zh-hans", "zh-cn", "zh-sg", "zh-hans-cn")] or candidates
        if candidates:
            mapped[language] = candidates[0]
    missing = sorted(set(LOCALES) - set(mapped))
    if missing:
        print(f"Not in the exported listing, so left out: {', '.join(missing)}", file=sys.stderr)
    return mapped


def zip_folder(folder: Path) -> Path:
    archive = folder.with_suffix(".zip")
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
        for file in sorted(folder.rglob("*")):
            z.write(file, file.relative_to(folder.parent).as_posix())
    return archive


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--images", type=Path, required=True, help="output of StoreListingScreenshotTests")
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--languages", help="comma separated, default: every text/<language>.json")
    parser.add_argument("--columns-from", type=Path, help="a listing CSV exported from Partner Center")
    parser.add_argument("--parts", type=int, default=5, help="how many folders to split all languages over (default 5)")
    args = parser.parse_args()

    locales = columns_from(args.columns_from)
    languages = args.languages.split(",") if args.languages else [l for l in LOCALES if (HERE / "text" / f"{l}.json").exists()]
    languages = [l for l in languages if l in locales]
    texts = {l: load_text(l) for l in languages}
    problems = [p for l, t in texts.items() for p in check(l, t)]
    for l in languages:
        for scene_index, scene in enumerate(SCREENSHOTS, 1):
            if not (args.images / l / "screenshots" / f"{scene_index:02d}-{scene}.png").exists():
                problems.append(f"{l}: screenshot {scene} was not rendered")
    for file in LOGOS.values():
        if not (args.images / "logos" / file).exists():
            problems.append(f"logo {file} was not rendered")
    if problems:
        sys.exit("\n".join(problems))

    args.out.mkdir(parents=True, exist_ok=True)

    def fresh(name: str) -> Path:
        folder = args.out / name
        if folder.exists():
            shutil.rmtree(folder)
        folder.mkdir()
        shutil.copytree(args.images / "logos", folder / "logos")
        return folder

    built = []
    parts = max(1, min(args.parts, len(languages)))
    for index in range(parts):
        # Languages in their usual order, spread evenly: the first parts take one more when they don't divide.
        group = languages[index * len(languages) // parts:(index + 1) * len(languages) // parts]
        root = f"valafind-store-listing-{index + 1}-of-{parts}"
        folder = fresh(root)
        for l in group:
            shutil.copytree(args.images / l / "screenshots", folder / l / "screenshots")
        write_csv(folder / "listing.csv", [locales[l] for l in group],
                  [values(texts[l], f"{root}/{l}/screenshots", f"{root}/logos") for l in group])
        print(f"{root}: {', '.join(group)}", file=sys.stderr)
        built.append(folder)

    for l in languages:
        root = f"valafind-store-listing-{l}"
        folder = fresh(root)
        shutil.copytree(args.images / l / "screenshots", folder / "screenshots")
        write_csv(folder / "listing.csv", [locales[l]], [values(texts[l], f"{root}/screenshots", f"{root}/logos")])
        built.append(folder)

    for folder in built:
        print(zip_folder(folder))


if __name__ == "__main__":
    main()
