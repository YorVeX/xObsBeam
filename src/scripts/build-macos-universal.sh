#!/bin/bash
# Build macOS Universal Binary (.plugin bundle) for xObsBeam
# Creates a single .plugin that contains both arm64 and x86_64 architectures.
#
# This script builds the .NET NativeAOT plugin and the bundled native libraries
# (QoirLib, density) for both architectures, then combines them with lipo into
# universal binaries. libjpeg-turbo (turbojpeg) is NOT bundled; it is expected to
# be installed via Homebrew (brew install libjpeg-turbo) on the target machine.

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SRC_DIR="$(dirname "$SCRIPT_DIR")"
PROJECT_ROOT="$(dirname "$SRC_DIR")"
OUTPUT_DIR="$SRC_DIR/publish"
LIB_DIR="$PROJECT_ROOT/lib"
PLUGIN_NAME="xObsBeam"
# xObsBeam uses <VersionPrefix> in the csproj (not <AssemblyVersion> like the reference project)
VERSION=$(grep -o '<VersionPrefix>[^<]*' "$SRC_DIR/xObsBeam.csproj" | sed 's/<VersionPrefix>//')

echo "=== Building $PLUGIN_NAME v$VERSION for macOS Universal ==="
echo "Setting MACOS_DEPLOYMENT_TARGET=12.0"
export MACOS_DEPLOYMENT_TARGET=12.0

# 1. Build for arm64
echo ""
echo "[1/7] Building for arm64..."
( cd "$SCRIPT_DIR" && bash build-osx-arm64.sh )

# 2. Build for x64
echo ""
echo "[2/7] Building for x64..."
( cd "$SCRIPT_DIR" && bash build-osx-x64.sh )

# 3. Create universal binaries with lipo
echo ""
echo "[3/7] Creating universal binaries..."
UNIVERSAL_DIR="$OUTPUT_DIR/macos-universal"
rm -rf "$UNIVERSAL_DIR"
mkdir -p "$UNIVERSAL_DIR"

# Combine the .NET plugin dylib
lipo -create \
  "$OUTPUT_DIR/osx-arm64/$PLUGIN_NAME.dylib" \
  "$OUTPUT_DIR/osx-x64/$PLUGIN_NAME.dylib" \
  -output "$UNIVERSAL_DIR/$PLUGIN_NAME.dylib"
echo "Universal plugin binary created: $UNIVERSAL_DIR/$PLUGIN_NAME.dylib"
lipo -info "$UNIVERSAL_DIR/$PLUGIN_NAME.dylib"

# Combine QoirLib
lipo -create \
  "$OUTPUT_DIR/osx-arm64/libQoirLib.dylib" \
  "$OUTPUT_DIR/osx-x64/libQoirLib.dylib" \
  -output "$UNIVERSAL_DIR/libQoirLib.dylib"
echo "Universal QoirLib binary created: $UNIVERSAL_DIR/libQoirLib.dylib"
lipo -info "$UNIVERSAL_DIR/libQoirLib.dylib"

# Combine density
lipo -create \
  "$OUTPUT_DIR/osx-arm64/libdensity.dylib" \
  "$OUTPUT_DIR/osx-x64/libdensity.dylib" \
  -output "$UNIVERSAL_DIR/libdensity.dylib"
echo "Universal density binary created: $UNIVERSAL_DIR/libdensity.dylib"
lipo -info "$UNIVERSAL_DIR/libdensity.dylib"

# 4. Create staging directory (browsable plain files, not a .plugin bundle)
echo ""
echo "[4/7] Creating staging directory..."

RELEASE_DIR="$SRC_DIR/release/macos-universal"
STAGING_ROOT="$RELEASE_DIR/staging"
BUNDLE_BIN_DIR="$STAGING_ROOT/bin"
BUNDLE_DATA_DIR="$STAGING_ROOT/data"

rm -rf "$STAGING_ROOT"
mkdir -p "$BUNDLE_BIN_DIR"
mkdir -p "$BUNDLE_DATA_DIR/locale"

# Copy the universal plugin binary (keeps .dylib name in staging, release script strips it for the plugin bundle)
cp "$UNIVERSAL_DIR/$PLUGIN_NAME.dylib" "$BUNDLE_BIN_DIR/$PLUGIN_NAME"

# Copy the bundled universal native libraries
cp "$UNIVERSAL_DIR/libQoirLib.dylib" "$BUNDLE_BIN_DIR/"
cp "$UNIVERSAL_DIR/libdensity.dylib" "$BUNDLE_BIN_DIR/"

# Copy locale files
if [ -d "$SRC_DIR/locale" ]; then
  cp "$SRC_DIR/locale/"*.ini "$BUNDLE_DATA_DIR/locale/"
  echo "Copied locale files ($(ls "$SRC_DIR/locale/"*.ini 2>/dev/null | wc -l | tr -d ' ') files: $(ls "$SRC_DIR/locale/"*.ini 2>/dev/null | xargs -n1 basename | tr '\n' ' '))"
fi

# 5. Create Info.plist in staging (metadata for the plugin bundle)
echo ""
echo "[5/7] Creating Info.plist..."
cat > "$STAGING_ROOT/Info.plist" << PLISTEOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>English</string>
    <key>CFBundleExecutable</key>
    <string>$PLUGIN_NAME</string>
    <key>CFBundleIdentifier</key>
    <string>com.yorvex.xobsbeam</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>$PLUGIN_NAME</string>
    <key>CFBundlePackageType</key>
    <string>BNDL</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>NSHumanReadableCopyright</key>
    <string>© 2023-2026 YorVeX, https://github.com/YorVeX. Licensed under MIT.</string>
    <key>CFBundleGetInfoString</key>
    <string>$VERSION, Copyright © 2023-2026 YorVeX</string>
    <key>MinimumOSVersion</key>
    <string>12.0</string>
</dict>
</plist>
PLISTEOF
echo "Info.plist created"

# 6. Verify staging structure
echo ""
echo "[6/7] Staging directory structure:"
find "$STAGING_ROOT" -type f | sort

# 7. Summary
echo ""
echo "[7/7] === Build complete! ==="
echo "Staging directory: $STAGING_ROOT"
echo "Universal binaries: $UNIVERSAL_DIR"
echo ""
echo "Note: libjpeg-turbo (turbojpeg) is NOT bundled. On the target machine install it via Homebrew:"
echo "  brew install libjpeg-turbo"
echo ""
echo "Next step: run release-macos-universal.sh to create the .plugin bundle and installers."
