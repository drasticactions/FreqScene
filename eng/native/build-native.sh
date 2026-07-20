#!/usr/bin/env bash
# Usage: eng/native/build-native.sh <rid>
#   rid: osx-arm64 | osx-x64 | linux-x64
set -euo pipefail

RID="${1:-}"
[[ -n "$RID" ]] || { echo "usage: $0 <osx-arm64|osx-x64|linux-x64>" >&2; exit 2; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SRC_DIR="$REPO_ROOT/external/projectm"
BUILD_DIR="$REPO_ROOT/artifacts/native-build/$RID"
STAGE_DIR="$REPO_ROOT/artifacts/native-install/$RID"
OUT_DIR="$REPO_ROOT/artifacts/native/$RID"

CMAKE_ARGS=(
  -DCMAKE_BUILD_TYPE=Release
  -DBUILD_SHARED_LIBS=ON
  -DENABLE_PLAYLIST=ON
  -DENABLE_SDL_UI=OFF
  -DENABLE_DEBUG_POSTFIX=OFF
  -DENABLE_INSTALL=ON
  -DBUILD_TESTING=OFF
  # Vendored subprojects declare pre-3.5 minimums that CMake 4.x rejects.
  -DCMAKE_POLICY_VERSION_MINIMUM=3.5
  "-DCMAKE_INSTALL_PREFIX=$STAGE_DIR"
)

case "$RID" in
  osx-arm64) CMAKE_ARGS+=(-DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0) ;;
  osx-x64)   CMAKE_ARGS+=(-DCMAKE_OSX_ARCHITECTURES=x86_64 -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0) ;;
  linux-x64) ;;
  *) echo "unsupported rid: $RID" >&2; exit 2 ;;
esac

git -C "$REPO_ROOT" submodule update --init --recursive external/projectm

cmake -S "$SRC_DIR" -B "$BUILD_DIR" "${CMAKE_ARGS[@]}"
cmake --build "$BUILD_DIR" --parallel
cmake --install "$BUILD_DIR"

rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

if [[ "$RID" == osx-* ]]; then
  LIBDIR="$STAGE_DIR/lib"
  # Dereference symlinks; ship versioned SONAME-equivalent names plus dev names.
  cp -L "$LIBDIR/libprojectM-4.4.dylib" "$OUT_DIR/libprojectM-4.4.dylib"
  cp -L "$LIBDIR/libprojectM-4.4.dylib" "$OUT_DIR/libprojectM-4.dylib"
  cp -L "$LIBDIR/libprojectM-4-playlist.4.dylib" "$OUT_DIR/libprojectM-4-playlist.4.dylib"
  cp -L "$LIBDIR/libprojectM-4-playlist.4.dylib" "$OUT_DIR/libprojectM-4-playlist.dylib"

  # Point the playlist libs' dependency on core at @loader_path so a flat
  # runtimes/<rid>/native directory resolves without rpath setup by the host.
  for f in "$OUT_DIR/libprojectM-4-playlist.4.dylib" "$OUT_DIR/libprojectM-4-playlist.dylib"; do
    dep="$(otool -L "$f" | awk '/libprojectM-4\.4?\.dylib/ && !/playlist/ {print $1; exit}')"
    if [[ -n "$dep" && "$dep" != "@loader_path/libprojectM-4.4.dylib" ]]; then
      install_name_tool -change "$dep" "@loader_path/libprojectM-4.4.dylib" "$f"
    fi
    codesign --force --sign - "$f"
  done
  codesign --force --sign - "$OUT_DIR/libprojectM-4.4.dylib" "$OUT_DIR/libprojectM-4.dylib"

  # Verify: exports present and playlist dependency rewritten.
  nm -gU "$OUT_DIR/libprojectM-4.4.dylib" | grep -q _projectm_create
  nm -gU "$OUT_DIR/libprojectM-4-playlist.4.dylib" | grep -q _projectm_playlist_create
  otool -L "$OUT_DIR/libprojectM-4-playlist.4.dylib" | grep -q '@loader_path/libprojectM-4.4.dylib'
else
  LIBDIR="$STAGE_DIR/lib"
  [[ -d "$LIBDIR" ]] || LIBDIR="$STAGE_DIR/lib64"
  # Real file under the SONAME (what the playlist lib's DT_NEEDED references),
  # plus the loader-friendly dev name.
  cp -L "$LIBDIR/libprojectM-4.so.4" "$OUT_DIR/libprojectM-4.so.4"
  cp -L "$LIBDIR/libprojectM-4.so.4" "$OUT_DIR/libprojectM-4.so"
  cp -L "$LIBDIR/libprojectM-4-playlist.so.4" "$OUT_DIR/libprojectM-4-playlist.so.4"
  cp -L "$LIBDIR/libprojectM-4-playlist.so.4" "$OUT_DIR/libprojectM-4-playlist.so"

  command -v patchelf >/dev/null || { echo "patchelf is required on linux" >&2; exit 1; }
  patchelf --set-rpath '$ORIGIN' "$OUT_DIR/libprojectM-4-playlist.so.4"
  patchelf --set-rpath '$ORIGIN' "$OUT_DIR/libprojectM-4-playlist.so"

  nm -D --defined-only "$OUT_DIR/libprojectM-4.so.4" | grep -q ' projectm_create'
  nm -D --defined-only "$OUT_DIR/libprojectM-4-playlist.so.4" | grep -q ' projectm_playlist_create'
fi

echo "native artifacts for $RID staged in $OUT_DIR:"
ls -la "$OUT_DIR"
