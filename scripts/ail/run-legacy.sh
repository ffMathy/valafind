#!/usr/bin/env bash
# Starts the existing Valafind on a virtual X11 desktop, for the AIL end-to-end checks (ail.config.json run.legacy).
# Needs: the .NET SDK from global.json, the model (tools/fetch-model.sh; VALAFIND_MODEL_DIR says where it is, default
# .model/embeddinggemma-2, as CI fetches it: tools/fetch-model.sh "$PWD/.model/embeddinggemma-2"), xvfb, openbox, x11-utils, xdotool, and for the tray icon xfce4-panel.
#   sudo apt-get install -y xvfb openbox x11-utils xdotool xfce4-panel
# The desktop is $AIL_DISPLAY (default :97). Valafind's home (the folders it indexes) and data folder live in a fresh
# $AIL_STATE (default .ail/legacy). Several copies can run side by side, each with its own AIL_DISPLAY and AIL_STATE.
# AIL_SKIP_BUILD=1 skips the build.
set -euo pipefail
cd "$(dirname "$0")/../.."
export PATH="$HOME/.dotnet:$PATH" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
model="${VALAFIND_MODEL_DIR:-$PWD/.model/embeddinggemma-2}"
state="${AIL_STATE:-.ail/legacy}"
rm -rf "$state" && mkdir -p "$state/home" "$state/data/models"
state="$(cd "$state" && pwd)"
[[ -f "$model/onnx/model_quantized.onnx" ]] || { echo "no model in $model: run tools/fetch-model.sh $model" >&2; exit 1; }
ln -s "$model" "$state/data/models/embeddinggemma-2"

if [[ "${AIL_SKIP_BUILD:-}" != 1 ]]; then
  dotnet build src/Valafind.App -c Release -v quiet -nologo >"$state/build.log"
fi

export DISPLAY="${AIL_DISPLAY:-:97}"
pids=()
cleanup() {
  for pid in "${pids[@]}"; do kill "$pid" 2>/dev/null || true; done
  for file in "$state/tray/panel.pid" "$state/tray/dbus.pid"; do [[ -f "$file" ]] && kill "$(cat "$file")" 2>/dev/null || true; done
}
trap cleanup EXIT INT TERM

if ! xdpyinfo >/dev/null 2>&1; then
  Xvfb "$DISPLAY" -screen 0 1280x800x24 -nolisten tcp >"$state/xvfb.log" 2>&1 &
  pids+=($!)
  for _ in $(seq 50); do xdpyinfo >/dev/null 2>&1 && break; sleep 0.1; done
fi
if ! xprop -root _NET_SUPPORTING_WM_CHECK 2>/dev/null | grep -q "window id #"; then
  openbox >"$state/openbox.log" 2>&1 &
  pids+=($!)
  sleep 1
fi
if command -v xfce4-panel >/dev/null; then
  eval "$(scripts/ail/tray-host.sh "$state/tray")"
fi

# Files and links Valafind opens are written to .ail/opened/<display>.txt instead of opening an app.
export PATH="$PWD/scripts/ail/bin:$PATH"

HOME="$state/home" XDG_DATA_HOME="$state/home/.local/share" XDG_CONFIG_HOME="$state/home/.config" \
VALAFIND_DATA_DIR="$state/data" VALAFIND_NO_INSTALL=1 VALAFIND_NO_UPDATE=1 VALAFIND_UI=linux \
  dotnet src/Valafind.App/bin/Release/net10.0/Valafind.dll --no-install >"$state/valafind.log" 2>&1 &
pids+=($!)
wait "${pids[-1]}"
