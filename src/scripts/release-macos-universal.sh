#!/bin/bash
# Release script for macOS Universal Binary - creates .tar.xz and .pkg
# Requires: build-macos-universal.sh already run successfully
#
# The .plugin bundle contains the universal xObsBeam binary plus the bundled
# universal native libraries (libQoirLib.dylib, libdensity.dylib). libjpeg-turbo
# (turbojpeg) is NOT bundled; it must be installed via Homebrew on the target.

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SRC_DIR="$(dirname "$SCRIPT_DIR")"
OUTPUT_DIR="$SRC_DIR/publish"
PLUGIN_NAME="xObsBeam"
# xObsBeam uses <VersionPrefix> in the csproj (not <AssemblyVersion> like the reference project)
VERSION=$(grep -o '<VersionPrefix>[^<]*' "$SRC_DIR/xObsBeam.csproj" | sed 's/<VersionPrefix>//')

STAGING_ROOT="$SRC_DIR/release/macos-universal/staging"
RELEASE_DIR="$SRC_DIR/release/macos-universal"
PACKAGE_NAME="${PLUGIN_NAME}-${VERSION}-macos-universal"

echo "=== Creating release packages for $PLUGIN_NAME v$VERSION ==="

# Check that the staging directory exists
if [ ! -d "$STAGING_ROOT" ]; then
  echo "ERROR: Staging directory not found at $STAGING_ROOT"
  echo "Run build-macos-universal.sh first!"
  exit 1
fi

# Check that required files exist
if [ ! -f "$STAGING_ROOT/bin/$PLUGIN_NAME" ]; then
  echo "ERROR: Plugin binary not found at $STAGING_ROOT/bin/$PLUGIN_NAME"
  exit 1
fi
if [ ! -f "$STAGING_ROOT/bin/libQoirLib.dylib" ]; then
  echo "ERROR: QoirLib native library not found at $STAGING_ROOT/bin/libQoirLib.dylib"
  exit 1
fi
if [ ! -f "$STAGING_ROOT/bin/libdensity.dylib" ]; then
  echo "ERROR: density native library not found at $STAGING_ROOT/bin/libdensity.dylib"
  exit 1
fi

# Create release directory
mkdir -p "$RELEASE_DIR"

# Create the .plugin bundle from staging files
echo ""
echo "[1/5] Creating .plugin bundle from staging..."
BUNDLE_DIR="$RELEASE_DIR/$PLUGIN_NAME.plugin"
rm -rf "$BUNDLE_DIR"
mkdir -p "$BUNDLE_DIR/Contents/MacOS"
mkdir -p "$BUNDLE_DIR/Contents/Resources/locale"

# Copy the binary (strip .dylib extension for macOS bundle loader)
cp "$STAGING_ROOT/bin/$PLUGIN_NAME" "$BUNDLE_DIR/Contents/MacOS/$PLUGIN_NAME"

# Copy the bundled native libraries next to the plugin binary
cp "$STAGING_ROOT/bin/libQoirLib.dylib" "$BUNDLE_DIR/Contents/MacOS/"
cp "$STAGING_ROOT/bin/libdensity.dylib" "$BUNDLE_DIR/Contents/MacOS/"

# Copy locale files
if [ -d "$STAGING_ROOT/data/locale" ]; then
  cp "$STAGING_ROOT/data/locale/"*.ini "$BUNDLE_DIR/Contents/Resources/locale/"
fi

# Copy Info.plist from staging into the bundle
cp "$STAGING_ROOT/Info.plist" "$BUNDLE_DIR/Contents/Info.plist"
echo "Info.plist copied from staging"

echo "Plugin bundle created: $BUNDLE_DIR"

# Validate the bundle
echo ""
echo "Validating plugin bundle..."
if [ -f "$BUNDLE_DIR/Contents/Info.plist" ]; then
  echo "  ✓ Info.plist found"
else
  echo "  ✗ Info.plist missing!"
  exit 1
fi
if [ -f "$BUNDLE_DIR/Contents/MacOS/$PLUGIN_NAME" ]; then
  echo "  ✓ Plugin binary found"
  lipo -info "$BUNDLE_DIR/Contents/MacOS/$PLUGIN_NAME" 2>/dev/null || echo "    (lipo info not available)"
else
  echo "  ✗ Plugin binary missing!"
  exit 1
fi
if [ -f "$BUNDLE_DIR/Contents/MacOS/libQoirLib.dylib" ]; then
  echo "  ✓ QoirLib native library found"
else
  echo "  ✗ QoirLib native library missing!"
  exit 1
fi
if [ -f "$BUNDLE_DIR/Contents/MacOS/libdensity.dylib" ]; then
  echo "  ✓ density native library found"
else
  echo "  ✗ density native library missing!"
  exit 1
fi
if [ -d "$BUNDLE_DIR/Contents/Resources/locale" ] && [ "$(ls -A "$BUNDLE_DIR/Contents/Resources/locale/" 2>/dev/null)" ]; then
  echo "  ✓ Locale files found"
else
  echo "  ⚠ Warning: No locale files found in Contents/Resources/locale/"
fi

echo ""
echo "Plugin bundle structure:"
find "$BUNDLE_DIR" -type f | sort

# 2. Create .tar.xz archive (for manual installation)
echo ""
echo "[2/5] Creating .tar.xz archive..."

# Create a temp directory with the plugin bundle wrapped in a PLUGIN_NAME folder
TAR_TEMP_DIR="$OUTPUT_DIR/macos-universal/_tar_temp"
rm -rf "$TAR_TEMP_DIR"
mkdir -p "$TAR_TEMP_DIR/$PLUGIN_NAME"

cp -R "$BUNDLE_DIR" "$TAR_TEMP_DIR/$PLUGIN_NAME/"

cd "$TAR_TEMP_DIR"
tar -cJf "$RELEASE_DIR/$PACKAGE_NAME.tar.xz" "$PLUGIN_NAME/"
rm -rf "$TAR_TEMP_DIR"
cd "$SRC_DIR"

echo "Created: $RELEASE_DIR/$PACKAGE_NAME.tar.xz"
ls -lh "$RELEASE_DIR/$PACKAGE_NAME.tar.xz"

# Verify tar contents
echo ""
echo "Verifying .tar.xz contents:"
tar -tJf "$RELEASE_DIR/$PACKAGE_NAME.tar.xz" | head -30

# 3. Create .pkg installer (requires pkgbuild, macOS only)
echo ""
echo "[3/5] Creating .pkg installer..."
if command -v pkgbuild &>/dev/null; then
  PKG_ROOT_DIR="$OUTPUT_DIR/macos-universal/_pkg_root"
  PKG_SCRIPTS_DIR="$OUTPUT_DIR/macos-universal/_pkg_scripts"
  rm -rf "$PKG_ROOT_DIR" "$PKG_SCRIPTS_DIR"
  mkdir -p "$PKG_ROOT_DIR"
  mkdir -p "$PKG_SCRIPTS_DIR"
  cp -R "$BUNDLE_DIR" "$PKG_ROOT_DIR/"

  # Write postinstall script that copies the plugin to the real user's home
  # The installer runs as root, so we determine the console user at install time
  cat > "$PKG_SCRIPTS_DIR/postinstall" << POSTINSTALLEOF
#!/bin/bash
# Postinstall script for $PLUGIN_NAME
# Copies the plugin bundle to the user's home directory
# (pkgbuild's --install-location cannot expand \$HOME or ~)

PLUGIN_NAME="$PLUGIN_NAME"

# Determine the console user's home directory
CONSOLE_USER=\$(stat -f "%Su" /dev/console 2>/dev/null || echo "")
if [ -z "\$CONSOLE_USER" ]; then
  CONSOLE_USER=\$(echo "show State:/Users/ConsoleUser" | scutil | awk '/Name :/ { print \$3 }' 2>/dev/null || echo "")
fi

if [ -n "\$CONSOLE_USER" ]; then
  USER_HOME="/Users/\$CONSOLE_USER"
  PLUGIN_DEST="\$USER_HOME/Library/Application Support/obs-studio/plugins"
  mkdir -p "\$PLUGIN_DEST"
  cp -R "/tmp/$PLUGIN_NAME-installer/\$PLUGIN_NAME.plugin" "\$PLUGIN_DEST/"
  chown -R "\$CONSOLE_USER:staff" "\$PLUGIN_DEST/\$PLUGIN_NAME.plugin"
  echo "Installed \$PLUGIN_NAME.plugin to \$PLUGIN_DEST"
else
  echo "WARNING: Could not determine console user. Plugin may not be installed correctly."
  exit 1
fi
exit 0
POSTINSTALLEOF

  chmod +x "$PKG_SCRIPTS_DIR/postinstall"

  pkgbuild \
    --root "$PKG_ROOT_DIR" \
    --scripts "$PKG_SCRIPTS_DIR" \
    --install-location "/tmp/$PLUGIN_NAME-installer" \
    --identifier "com.yorvex.xobsbeam" \
    --version "$VERSION" \
    "$RELEASE_DIR/$PACKAGE_NAME.pkg"

  rm -rf "$PKG_ROOT_DIR" "$PKG_SCRIPTS_DIR"
  echo "Created: $RELEASE_DIR/$PACKAGE_NAME.pkg"
  ls -lh "$RELEASE_DIR/$PACKAGE_NAME.pkg"

  if command -v pkgutil &>/dev/null; then
    echo ""
    echo "Verifying .pkg..."
    pkgutil --check-signature "$RELEASE_DIR/$PACKAGE_NAME.pkg" 2>/dev/null || echo "  Note: Package is not signed (Developer ID needed for signing)"
    echo ""
    echo "Package contents:"
    pkgutil --payload-files "$RELEASE_DIR/$PACKAGE_NAME.pkg" | head -30
  fi
else
  echo "WARNING: pkgbuild not found. Skipping .pkg creation."
fi

# 5. Create uninstaller .pkg (separate identifier so postinstall can forget the installer receipt cleanly)
echo ""
echo "[5/5] Creating uninstaller .pkg..."
if command -v pkgbuild &>/dev/null; then
  UNINSTALLER_DIR="$OUTPUT_DIR/macos-universal/_uninstaller_temp"
  UNINSTALLER_SCRIPTS_DIR="$UNINSTALLER_DIR/scripts"
  rm -rf "$UNINSTALLER_DIR"
  mkdir -p "$UNINSTALLER_SCRIPTS_DIR"

  INSTALLER_IDENTIFIER="com.yorvex.xobsbeam"
  UNINSTALLER_IDENTIFIER="${INSTALLER_IDENTIFIER}.uninstaller"

  # Write the postinstall script that removes files then forgets the receipts
  cat > "$UNINSTALLER_SCRIPTS_DIR/postinstall" << UNINSTALLEOF
#!/bin/bash
# Uninstaller postinstall script for $PLUGIN_NAME
# Removes all plugin files and cleans up the pkgutil receipts

PLUGIN_NAME="$PLUGIN_NAME"
INSTALLER_IDENTIFIER="$INSTALLER_IDENTIFIER"
UNINSTALLER_IDENTIFIER="$UNINSTALLER_IDENTIFIER"

# Determine the console user's home directory (same logic as installer postinstall)
CONSOLE_USER=\$(stat -f "%Su" /dev/console 2>/dev/null || echo "")
if [ -z "\$CONSOLE_USER" ]; then
  CONSOLE_USER=\$(echo "show State:/Users/ConsoleUser" | scutil | awk '/Name :/ { print \$3 }' 2>/dev/null || echo "")
fi

if [ -n "\$CONSOLE_USER" ]; then
  OBS_PLUGINS_DIR="/Users/\$CONSOLE_USER/Library/Application Support/obs-studio/plugins"
else
  OBS_PLUGINS_DIR="\$HOME/Library/Application Support/obs-studio/plugins"
fi

echo "Uninstalling \$PLUGIN_NAME..."

# Remove the .plugin bundle
if [ -d "\$OBS_PLUGINS_DIR/\$PLUGIN_NAME.plugin" ]; then
  rm -rf "\$OBS_PLUGINS_DIR/\$PLUGIN_NAME.plugin"
  echo "  Removed \$OBS_PLUGINS_DIR/\$PLUGIN_NAME.plugin"
fi

# Forget the installer receipt (this sticks because the framework writes the
# uninstaller receipt, not the installer one)
if command -v pkgutil &>/dev/null; then
  pkgutil --forget "\$INSTALLER_IDENTIFIER" 2>/dev/null && echo "  Cleared installer receipt"
fi

# Schedule our own receipt to be forgotten in the background after the
# installer framework finishes writing it
(sleep 3; pkgutil --forget "\$UNINSTALLER_IDENTIFIER" 2>/dev/null; rm -f /tmp/\$PLUGIN_NAME-uninstaller-\$\$) &
SCHEDULED_PID=\$!
echo "  Scheduled cleanup (PID \$SCHEDULED_PID)"

echo "Uninstall complete."
exit 0
UNINSTALLEOF

  chmod +x "$UNINSTALLER_SCRIPTS_DIR/postinstall"

  UNINSTALLER_PKG_NAME="${PLUGIN_NAME}-${VERSION}-macos-universal-uninstaller"

  pkgbuild \
    --root "$UNINSTALLER_DIR" \
    --scripts "$UNINSTALLER_SCRIPTS_DIR" \
    --identifier "$UNINSTALLER_IDENTIFIER" \
    --version "$VERSION" \
    --install-location "/tmp/$PLUGIN_NAME-uninstaller" \
    "$RELEASE_DIR/$UNINSTALLER_PKG_NAME.pkg"

  rm -rf "$UNINSTALLER_DIR"
  echo "Created: $RELEASE_DIR/$UNINSTALLER_PKG_NAME.pkg"
  ls -lh "$RELEASE_DIR/$UNINSTALLER_PKG_NAME.pkg"
else
  echo "WARNING: pkgbuild not found. Skipping uninstaller .pkg creation."
fi

echo ""
echo "=== Release complete! ==="
echo ""
echo "Files in $RELEASE_DIR:"
ls -lh "$RELEASE_DIR/"
echo ""
echo "The .tar.xz and installer .pkg contain:"
echo "  $PLUGIN_NAME/"
echo "  └── $PLUGIN_NAME.plugin/        (OBS plugin bundle, native libs in Contents/MacOS/, locale files in Contents/Resources/locale/)"
echo ""
echo "The uninstaller .pkg removes the plugin bundle."
echo ""
echo "Prerequisite on the target machine (for JPEG compression):"
echo "  brew install libjpeg-turbo"
echo ""
echo "Installation (manual):"
echo "  tar -xJf $PACKAGE_NAME.tar.xz -C ~/'Library/Application Support/obs-studio/plugins/'"
echo ""
echo "Uninstallation:"
echo "  open ${PLUGIN_NAME}-${VERSION}-macos-universal-uninstaller.pkg"
echo ""
echo "For signing (requires Apple Developer account):"
echo "  codesign --force --deep --sign 'Developer ID Application: ...' '$BUNDLE_DIR'"
echo "  pkgbuild --root '<dir-with-only-plugin-bundle>' --install-location ... --sign 'Developer ID Installer: ...' '$PACKAGE_NAME.pkg'"
