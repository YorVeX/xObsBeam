#!/bin/bash
# Test deployment script for macOS - copies built plugin to OBS plugins folder
# Deploys the single-architecture build for the current machine plus the bundled
# native libraries (libQoirLib.dylib, libdensity.dylib). libjpeg-turbo (turbojpeg)
# is expected to be available via Homebrew on this machine.

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SRC_DIR="$(dirname "$SCRIPT_DIR")"
PROJECT_ROOT="$(dirname "$SRC_DIR")"
LIB_DIR="$PROJECT_ROOT/lib"

echo "=== Deploying xObsBeam to OBS plugins folder for testing ==="

# Detect architecture
ARCH=$(uname -m)
if [ "$ARCH" = "arm64" ]; then
    BUILD_DIR="$SRC_DIR/publish/osx-arm64"
    echo "Detected Apple Silicon (ARM64)"
elif [ "$ARCH" = "x86_64" ]; then
    BUILD_DIR="$SRC_DIR/publish/osx-x64"
    echo "Detected Intel (x64)"
else
    echo "Unknown architecture: $ARCH"
    exit 1
fi

if [ ! -f "$BUILD_DIR/xObsBeam.dylib" ]; then
    echo "Error: Built plugin not found at $BUILD_DIR/xObsBeam.dylib"
    echo "Please run the build task first (build-osx-arm64.sh or build-osx-x64.sh)!"
    exit 1
fi

# OBS plugins directory
OBS_PLUGINS_DIR="$HOME/Library/Application Support/obs-studio/plugins"

echo "Removing any leftover xObsBeam.plugin directory from previous runs..."
rm -rf "$OBS_PLUGINS_DIR/xObsBeam.plugin"

echo "Creating plugin data directory structure..."
mkdir -p "$OBS_PLUGINS_DIR/xObsBeam.plugin/Contents/MacOS"
mkdir -p "$OBS_PLUGINS_DIR/xObsBeam.plugin/Contents/Resources/locale"

echo "Copying plugin binary as xObsBeam.plugin..."
cp "$BUILD_DIR/xObsBeam.dylib" "$OBS_PLUGINS_DIR/xObsBeam.plugin/Contents/MacOS/xObsBeam"

# Copy the bundled native libraries next to the plugin binary
echo "Copying bundled native libraries (libQoirLib.dylib, libdensity.dylib)..."
cp "$BUILD_DIR/libQoirLib.dylib" "$OBS_PLUGINS_DIR/xObsBeam.plugin/Contents/MacOS/"
cp "$BUILD_DIR/libdensity.dylib" "$OBS_PLUGINS_DIR/xObsBeam.plugin/Contents/MacOS/"

# Copy locale files
echo "Copying locale files..."
cp "$SRC_DIR/locale/"*.ini "$OBS_PLUGINS_DIR/xObsBeam.plugin/Contents/Resources/locale/"

echo ""
echo "=== Deployment complete! ==="
echo "Plugin installed at: $OBS_PLUGINS_DIR/xObsBeam.plugin"
echo ""
echo "Note: libjpeg-turbo (turbojpeg) is NOT bundled. For JPEG compression install it via Homebrew:"
echo "  brew install libjpeg-turbo"
echo ""
echo "Restart OBS Studio to load the plugin."
