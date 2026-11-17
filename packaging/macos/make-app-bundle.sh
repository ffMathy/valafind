#!/usr/bin/env bash
# Wraps a `dotnet publish` output folder into Valafind.app.
#
#   packaging/macos/make-app-bundle.sh <publish-dir> <output-dir> [version]
#
# The bundle runs as a menu-bar (LSUIElement) app. Release builds embed the EmbeddingGemma 2 model in the executable
# (published with -p:ValafindModelDir=...). For a build without it, a model folder placed at
# Valafind.app/Contents/Resources/models/embeddinggemma-2 is found automatically (see ModelLocator);
# otherwise the app asks the user to run Contents/MacOS/tools/fetch-model.sh once.
# Builds an .icns from the app PNG when the macOS tools (sips, iconutil) are available.
set -euo pipefail

publish_dir="${1:?publish dir}"
out_dir="${2:?output dir}"
version="${3:-0.1.0}"
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"

app="$out_dir/Valafind.app"
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$publish_dir"/. "$app/Contents/MacOS/"
chmod +x "$app/Contents/MacOS/Valafind"
sed "s/__VERSION__/$version/g" "$here/Info.plist" > "$app/Contents/Info.plist"
printf 'APPL????' > "$app/Contents/PkgInfo"

png="$repo/src/Valafind.App/Assets/valafind.png"
if command -v sips >/dev/null && command -v iconutil >/dev/null && [ -f "$png" ]; then
  iconset="$(mktemp -d)/Valafind.iconset"
  mkdir -p "$iconset"
  for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" "$png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
  done
  iconutil -c icns "$iconset" -o "$app/Contents/Resources/Valafind.icns"
fi

# Ad-hoc signature so Gatekeeper on Apple Silicon will run the unsigned build locally.
if command -v codesign >/dev/null; then
  codesign --force --deep --sign - "$app" >/dev/null 2>&1 || echo "warning: ad-hoc codesign failed" >&2
fi
echo "Created $app"
