#!/usr/bin/env bash
# deploy.sh — Build School Buses and copy to the game's Mods folder.
#
# Reads configuration from .env in the repo root.
# See .env.example for required variables.
#
# Usage:
#   ./deploy.sh           → Debug build
#   ./deploy.sh --release → Release build

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ -f "$SCRIPT_DIR/.env" ]]; then
    while IFS='=' read -r _key _value; do
        [[ "$_key" =~ ^[[:space:]]*# ]] && continue
        [[ -z "$_key" ]]               && continue
        _value="${_value%\"}"  ; _value="${_value#\"}"
        _value="${_value%\'}"  ; _value="${_value#\'}"
        export "$_key=$_value"
    done < "$SCRIPT_DIR/.env"
    unset _key _value
fi

CONFIGURATION="Debug"
if [[ "${1:-}" == "--release" ]]; then
    CONFIGURATION="Release"
fi

MOD_NAME="SchoolBuses"
BUILD_OUT="$SCRIPT_DIR/bin/$CONFIGURATION"

DATA_MOUNT="${CITIES_DATA_MOUNT:-/mnt/cities_skylines_data}"
WORKSHOP_MOUNT="${CITIES_WORKSHOP_MOUNT:-/mnt/cities_workshop}"
WORKSHOP_ITEM_ID="${WORKSHOP_ITEM_ID:-}"
MODS_DIR="$DATA_MOUNT/Addons/Mods/$MOD_NAME"
LOG_FILE="$DATA_MOUNT/output_log.txt"

if ! mountpoint -q "$DATA_MOUNT"; then
    echo "ERROR: $DATA_MOUNT is not mounted."
    echo "Run mount-cities.sh first."
    exit 1
fi

echo "Building ($CONFIGURATION)..."
cd "$SCRIPT_DIR"
xbuild SchoolBuses.csproj /p:Configuration="$CONFIGURATION" /nologo /verbosity:quiet
echo "Build succeeded."

# ── Stage to dist/ ───────────────────────────────────────────────────────────────
DIST="$SCRIPT_DIR/dist/$MOD_NAME"
rm -rf "$DIST"
mkdir -p "$DIST"
cp "$BUILD_OUT/SchoolBuses.dll" "$DIST/SchoolBuses.dll"
echo ""
echo "Staged to: $DIST"
ls -lh "$DIST"

# ── Deploy to game ────────────────────────────────────────────────────────────────
echo ""
echo "Copying to $MODS_DIR ..."
mkdir -p "$MODS_DIR"
cp "$DIST/SchoolBuses.dll" "$MODS_DIR/"
echo "Done. Files in game Mods folder:"
ls -lh "$MODS_DIR"

# ── Copy to Workshop folder (for in-game Update dialog) ──────────────────────────
if [[ -n "$WORKSHOP_ITEM_ID" ]]; then
    WORKSHOP_MOD_DIR="$WORKSHOP_MOUNT/content/255710/$WORKSHOP_ITEM_ID"
    if [[ -d "$WORKSHOP_MOD_DIR" ]]; then
        cp "$DIST/SchoolBuses.dll" "$WORKSHOP_MOD_DIR/"
        cp "$SCRIPT_DIR/Workshop/PreviewImage.png" "$WORKSHOP_MOD_DIR/"
        echo ""
        echo "Copied to Workshop folder: $WORKSHOP_MOD_DIR"
        ls -lh "$WORKSHOP_MOD_DIR"
    else
        echo "Workshop folder not found (not subscribed?): $WORKSHOP_MOD_DIR"
    fi
fi

echo ""
if [[ -f "$LOG_FILE" ]]; then
    echo "════════════════════  output_log.txt (last 60 lines)  ════════════════════"
    tail -n 60 "$LOG_FILE"
    echo "══════════════════════════════════════════════════════════════════════════"
else
    echo "Log not found at $LOG_FILE"
fi
