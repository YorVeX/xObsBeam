#!/bin/bash
# Build script for macOS x64 (Intel) - xObsBeam OBS Plugin
# Builds the .NET NativeAOT plugin and the bundled native libraries (QoirLib, density) for x64.
# libjpeg-turbo is NOT bundled; it is expected to be installed via Homebrew (brew install libjpeg-turbo).

# Exit on error
set -e

echo "=== Building xObsBeam for macOS x64 (Intel) ==="

# Define paths
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SRC_DIR="$(dirname "$SCRIPT_DIR")"
PROJECT_ROOT="$(dirname "$SRC_DIR")"
OUTPUT_DIR="$SRC_DIR/publish/osx-x64"
LIB_DIR="$PROJECT_ROOT/lib"

echo "Source directory: $SRC_DIR"
echo "Output directory: $OUTPUT_DIR"
echo "Lib directory: $LIB_DIR"

export MACOS_DEPLOYMENT_TARGET=12.0

# Clean previous build
echo "Cleaning previous build..."
rm -rf "$OUTPUT_DIR"

# 1. Build the bundled native libraries for x64
echo ""
echo "[1/3] Building native libraries for x64..."

# QoirLib
echo "  Building QoirLib..."
( cd "$LIB_DIR/QoirLib" && make clean && make ARCH=x86_64 )

# Density
echo "  Building density..."
( cd "$LIB_DIR/density/density" && make clean && make library ARCH=x86_64 )

# 2. Build the .NET plugin with NativeAOT as shared library
echo ""
echo "[2/3] Building .NET plugin..."
dotnet publish "$SRC_DIR" \
  -c Release \
  -o "$OUTPUT_DIR" \
  -r osx-x64 \
  /p:DefineConstants=MACOS \
  /p:NativeLib=Shared \
  /p:SelfContained=true

# 3. Copy the bundled native libraries next to the plugin
echo ""
echo "[3/3] Copying bundled native libraries..."
cp "$LIB_DIR/QoirLib/libQoirLib.dylib" "$OUTPUT_DIR/"
cp "$LIB_DIR/density/density/build/libdensity.dylib" "$OUTPUT_DIR/"

echo ""
echo "=== Build complete! ==="
echo "Output directory: $OUTPUT_DIR"
echo "Plugin file: $OUTPUT_DIR/xObsBeam.dylib"
echo "Bundled native libraries: libQoirLib.dylib, libdensity.dylib"
echo ""
echo "Note: libjpeg-turbo (turbojpeg) is NOT bundled. Install it via Homebrew:"
echo "  brew install libjpeg-turbo"
echo ""
echo "To install manually, copy to:"
echo "  ~/Library/Application Support/obs-studio/plugins/xObsBeam.plugin"
echo ""
