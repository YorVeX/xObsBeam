#!/bin/bash
# Rebuild the prebuilt Linux native libraries (QoirLib, density) in the WSL
# Ubuntu 22.04 (glibc 2.35) environment.
#
# Run from WSL:  bash rebuild-linux-libs.sh
#
# Prerequisites (installed by build-linux-x64-wsl-setup.cmd):
#   clang, clang++, make

set -e

LIB_DIR="/mnt/e/#cODING#/C#/#Streaming-Tools/xObsBeam/lib"
OUT_QOIR="$LIB_DIR/QoirLib/binaries/linux-x64-glibc-2.35"
OUT_DENSITY="$LIB_DIR/Density/binaries/linux-x64-glibc-2.35"

echo "=== Rebuilding native libraries for linux-x64 (glibc 2.35) ==="
echo "glibc version: $(ldd --version | head -1)"
echo "compiler:      $(clang++ --version | head -1)"
echo ""

# 1. QoirLib (C++, uses CXX which defaults to g++; override to clang++)
echo "[1/2] Building QoirLib..."
( cd "$LIB_DIR/QoirLib" && make clean && make CXX=clang++ )
mkdir -p "$OUT_QOIR"
cp -f "$LIB_DIR/QoirLib/libQoirLib.so" "$OUT_QOIR/libQoirLib.so"
echo "  -> $OUT_QOIR/libQoirLib.so"
echo ""

# 2. Density (C, uses CC which defaults to cc/gcc; override to clang)
#    Also remove stale .d dependency files left over from other platforms
#    (e.g. macOS paths) that would break the build.
echo "[2/2] Building density..."
( cd "$LIB_DIR/Density/density" && find . -name "*.d" -delete && rm -rf build && make library CC=clang )
mkdir -p "$OUT_DENSITY"
cp -f "$LIB_DIR/Density/density/build/libdensity.so" "$OUT_DENSITY/libdensity.so"
echo "  -> $OUT_DENSITY/libdensity.so"
echo ""

echo "=== Done. Verifying glibc requirements... ==="
for lib in "$OUT_QOIR/libQoirLib.so" "$OUT_DENSITY/libdensity.so"; do
  echo "--- $lib ---"
  echo "  NEEDED: $(readelf -d "$lib" | grep NEEDED)"
  echo "  GLIBC symbols used:"
  objdump -T "$lib" | grep -oE "GLIBC_[0-9.]+" | sort -u | sed 's/^/    /'
  echo "  file: $(file "$lib" | cut -d: -f2)"
done
