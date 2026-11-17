#!/usr/bin/env bash
# Downloads EmbeddingGemma 2 (ONNX, q8: text model, vision encoder, audio encoder; ~850 MB) for Valafind.
#
#   tools/fetch-model.sh [target-dir] [--force]
#
# Default target is the per-user models folder Valafind looks in:
#   macOS:   ~/Library/Application Support/Valafind/models/embeddinggemma-2
#   Linux:   ${XDG_DATA_HOME:-~/.local/share}/valafind/models/embeddinggemma-2
#   Windows: %LOCALAPPDATA%\Valafind\models\embeddinggemma-2   (prefer tools/fetch-model.ps1 there)
# VALAFIND_DATA_DIR overrides the data folder, as it does for the app.
#
# This one-time download is the ONLY network access involved; the Valafind app itself never downloads anything.
set -euo pipefail

BASE_URL="https://huggingface.co/onnx-community/embeddinggemma-2-ONNX/resolve/main"
FILES=(
  tokenizer.json
  tokenizer_config.json
  config.json
  processor_config.json
  preprocessor_config.json
  onnx/model_quantized.onnx
  onnx/model_quantized.onnx_data
  onnx/vision_encoder_quantized.onnx
  onnx/vision_encoder_quantized.onnx_data
  onnx/audio_encoder_quantized.onnx
  onnx/audio_encoder_quantized.onnx_data
)

force=0
target=""
custom=0
for arg in "$@"; do
  case "$arg" in
    -f|--force) force=1 ;;
    -h|--help) sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) if [ -z "$target" ]; then target="$arg"; custom=1; else echo "Unexpected argument: $arg" >&2; exit 2; fi ;;
  esac
done

if [ -z "$target" ]; then
  if [ -n "${VALAFIND_DATA_DIR:-}" ]; then
    data="$VALAFIND_DATA_DIR"
  else
    case "$(uname -s)" in
      Darwin) data="$HOME/Library/Application Support/Valafind" ;;
      MINGW*|MSYS*|CYGWIN*) data="${LOCALAPPDATA:?LOCALAPPDATA is not set}/Valafind" ;;
      *) data="${XDG_DATA_HOME:-$HOME/.local/share}/valafind" ;;
    esac
  fi
  target="$data/models/embeddinggemma-2"
fi

if command -v curl >/dev/null 2>&1; then
  download() { curl -fL --retry 3 --retry-delay 2 --progress-bar -o "$2" "$1"; }
elif command -v wget >/dev/null 2>&1; then
  download() { wget -q --show-progress --tries=3 -O "$2" "$1"; }
else
  echo "Error: curl or wget is required." >&2
  exit 1
fi

cat <<'EOF'
EmbeddingGemma 2 is provided by Google DeepMind under the Apache License 2.0:
  https://huggingface.co/google/embeddinggemma-2

This one-time download is the only network access Valafind needs.
The Valafind app itself never downloads anything and runs fully offline.

EOF

echo "Target: $target"
mkdir -p "$target/onnx"

for f in "${FILES[@]}"; do
  dest="$target/$f"
  if [ "$force" -eq 0 ] && [ -s "$dest" ]; then
    echo "  [skip] $f (already present)"
    continue
  fi
  echo "  [get]  $f"
  tmp="$dest.part"
  rm -f "$tmp"
  if ! download "$BASE_URL/$f?download=true" "$tmp"; then
    rm -f "$tmp"
    echo "Error: failed to download $f" >&2
    exit 1
  fi
  mv -f "$tmp" "$dest"
done

echo
if [ "$custom" -eq 1 ]; then
  echo "Done. Model installed in: $target"
  echo "Custom location: point ModelDirectory in settings.json (or VALAFIND_MODEL_DIR) at this folder."
else
  echo "Done. Valafind will find the model automatically in: $target"
fi
