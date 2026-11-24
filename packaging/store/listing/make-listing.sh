#!/usr/bin/env bash
# Regenerates the Microsoft Store listing import folders and ZIPs: renders the screenshots and logos from the real UI
# (StoreListingScreenshotTests), then fills Partner Center's listing CSV from text/<language>.json.
#
#   packaging/store/listing/make-listing.sh [out-dir] [-- build_listing.py options, e.g. --columns-from export.csv]
#
# Needs the .NET SDK, Python 3 and fonts for every script (Debian/Ubuntu: fonts-noto-core fonts-noto-cjk
# fonts-noto-ui-core). Set VALAFIND_STORE_LANGUAGES=en,de to render only some languages.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../../.." && pwd)"
out="$(realpath -m "${1:-$repo/artifacts/store-listing}")"
shift || true
[[ "${1:-}" == "--" ]] && shift

if command -v fc-list >/dev/null && ! fc-list | grep -qi "Noto Sans CJK"; then
  echo "warning: no Noto CJK font found; Chinese, Japanese and Korean text may render as boxes" >&2
fi

images="$out/rendered"
rm -rf "${images:?}"
mkdir -p "$images"

VALAFIND_STORE_LISTING_DIR="$images" dotnet test "$repo/tests/Valafind.App.Tests" -c Release \
  -- --filter-class "*StoreListingScreenshotTests"

python3 "$here/build_listing.py" --images "$images" --out "$out" ${VALAFIND_STORE_LANGUAGES:+--languages "$VALAFIND_STORE_LANGUAGES"} "$@"
