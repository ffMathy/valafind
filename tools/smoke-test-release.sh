#!/usr/bin/env bash
# Starts a file from a release the way a user would, and checks that the app comes up.
#
#   tools/smoke-test-release.sh <release file>
#
#   Windows  runs the .exe.
#   macOS    a .app.zip is unpacked with ditto (what Finder's Archive Utility does), the bundle is checked (Info.plist,
#            executable bit, code signature) and opened with `open`, which goes through Launch Services like a
#            double-click in Finder. A bare executable is made executable and run.
#   Linux    the file is made executable (browsers drop the bit) and run, on a virtual X display when there is none.
#
# The app installs itself as it does for users so run this on a throwaway
# machine such as a CI runner. It passes once the log says the app is ready and the app is still running a few
# seconds later; it then stops the app.
set -euo pipefail

app="Valafind"
linux_name="valafind"
# Logged once the user interface is up (AppHost.Start, Services/Log.cs).
ready="Valafind .* starting ("
# The file log's level for critical errors (Services/FileLogging.cs).
fatal=" FATAL "
# The first start unpacks the ~850 MB model.
timeout="${SMOKE_TIMEOUT:-300}"

file="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
# The real path: on macOS, mktemp answers /var/… while processes run from /private/var/….
work="$(cd "$(mktemp -d)" && pwd -P)"
case "$(uname -s)" in
  Darwin) os=macos; logs="$HOME/Library/Application Support/$app/logs" ;;
  MINGW* | MSYS* | CYGWIN*) os=windows; logs="$(cygpath -u "$LOCALAPPDATA")/$app/logs" ;;
  *) os=linux; logs="${XDG_DATA_HOME:-$HOME/.local/share}/$linux_name/logs" ;;
esac

running() {
  case "$os" in
    windows) powershell -NoProfile -Command "if (Get-Process '$app*' -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }" ;;
    macos) pgrep -f "/Contents/MacOS/$app( |\$)|^$work/" > /dev/null ;;
    linux) pgrep -f "^$work/|/\.local/bin/$linux_name( |\$)" > /dev/null ;;
  esac
}

stop() {
  case "$os" in
    windows) powershell -NoProfile -Command "Get-Process '$app*' -ErrorAction SilentlyContinue | Stop-Process -Force" || true ;;
    macos) pkill -f "/Contents/MacOS/$app( |\$)|^$work/" || true ;;
    linux) pkill -f "^$work/|/\.local/bin/$linux_name( |\$)" || true ;;
  esac
}

xvfb=""
cleanup() {
  stop
  if [[ -n "$xvfb" ]]; then kill "$xvfb" 2> /dev/null || true; fi
}
trap cleanup EXIT

fail() {
  echo "::error::$1"
  echo "----- output"
  cat "$work/output.log" 2> /dev/null || true
  echo "----- log ($logs)"
  cat "$logs"/* 2> /dev/null || echo "(no log)"
  exit 1
}

stop
rm -rf "$logs"

case "$file" in
  *.app.zip)
    [[ "$os" == macos ]] || fail "$file is a macOS app bundle"
    ditto -x -k "$file" "$work"
    bundle="$work/$app.app"
    [[ -d "$bundle" ]] || fail "$(basename "$file") holds no $app.app: $(ls "$work")"
    plutil -lint "$bundle/Contents/Info.plist"
    executable="$bundle/Contents/MacOS/$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$bundle/Contents/Info.plist")"
    [[ -x "$executable" ]] || fail "$executable is missing or not executable"
    codesign --verify --deep --strict --verbose=2 "$bundle" || fail "$bundle has no valid code signature"
    echo "Opening $bundle"
    open -n --stdout "$work/output.log" --stderr "$work/output.log" "$bundle" || fail "open could not start $bundle"
    ;;
  *)
    executable="$work/$(basename "$file")"
    cp "$file" "$executable"
    if [[ "$os" != windows ]]; then chmod +x "$executable"; fi
    if [[ "$os" == linux && -z "${DISPLAY:-}" ]]; then
      export DISPLAY=:98
      Xvfb "$DISPLAY" -screen 0 1280x800x24 -nolisten tcp &
      xvfb=$!
      sleep 2
    fi
    echo "Starting $executable"
    "$executable" > "$work/output.log" 2>&1 &
    ;;
esac

for ((waited = 0; waited < timeout; waited++)); do
  if grep -rqs "$ready" "$logs"; then break; fi
  sleep 1
done
grep -rqs "$ready" "$logs" || fail "$app did not report \"$ready\" within $timeout seconds"
echo "$app is ready after about $waited seconds"

sleep 10
running || fail "$app stopped within 10 seconds of starting"
if grep -rEqs "$fatal" "$logs"; then fail "$app logged a fatal error"; fi
echo "$app started from $(basename "$file") and is running"
