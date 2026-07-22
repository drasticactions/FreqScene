#!/usr/bin/env bash
# Usage: eng/native/build-apple.sh <rid>
#   rid: iossimulator-arm64 | ios-arm64 | tvossimulator-arm64 | tvos-arm64
#
# Cross-compiles libprojectM as a STATIC library for an Apple mobile platform.
# The consuming head (FreqScene.iOS / FreqScene.tvOS) links it into the app
# executable via a <NativeReference Kind="Static"> and reaches the C API through
# NativeLibrary.GetMainProgramHandle() + dlsym (see NativeLoader). For that dlsym
# to work the projectm_* symbols must be exported by the app linker, which
# requires (a) default symbol visibility in the native build and (b) the app
# declaring them as ReferenceNativeSymbol items (done in the csproj). Both are
# why patches/apple.patch is applied transiently below.
set -euo pipefail

RID="${1:-}"
[[ -n "$RID" ]] || { echo "usage: $0 <iossimulator-arm64|ios-arm64|tvossimulator-arm64|tvos-arm64>" >&2; exit 2; }

case "$RID" in
  iossimulator-arm64) SYSTEM=iOS;  SYSROOT=iphonesimulator ;;
  ios-arm64)          SYSTEM=iOS;  SYSROOT=iphoneos ;;
  tvossimulator-arm64) SYSTEM=tvOS; SYSROOT=appletvsimulator ;;
  tvos-arm64)          SYSTEM=tvOS; SYSROOT=appletvos ;;
  *) echo "unsupported rid: $RID" >&2; exit 2 ;;
esac

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SRC_DIR="$REPO_ROOT/external/projectm"
BUILD_DIR="$REPO_ROOT/artifacts/native-build/$RID"
OUT_DIR="$REPO_ROOT/artifacts/native/$RID"
OUT_LIB="$OUT_DIR/libprojectM-4-ios.a"
PATCH="$REPO_ROOT/eng/native/patches/apple.patch"

CMAKE_ARGS=(
  -G "Unix Makefiles"
  -DCMAKE_SYSTEM_NAME="$SYSTEM"
  -DCMAKE_OSX_SYSROOT="$SYSROOT"
  -DCMAKE_OSX_ARCHITECTURES=arm64
  -DCMAKE_OSX_DEPLOYMENT_TARGET=13.0
  -DCMAKE_BUILD_TYPE=Release
  -DBUILD_SHARED_LIBS=OFF
  # iOS/tvOS have no desktop GL; unlike Android, CMAKE_SYSTEM_NAME does not force
  # projectM's GLES profile on.
  -DENABLE_GLES=ON
  -DENABLE_PLAYLIST=OFF
  -DENABLE_SDL_UI=OFF
  -DENABLE_DEBUG_POSTFIX=OFF
  -DENABLE_INSTALL=OFF
  -DBUILD_TESTING=OFF
  # Vendored subprojects declare pre-3.5 minimums that CMake 4.x rejects.
  -DCMAKE_POLICY_VERSION_MINIMUM=3.5
)

git -C "$REPO_ROOT" submodule update --init --recursive external/projectm

# Apply the Apple source patch transiently; always revert so the submodule stays
# clean for git (even on failure).
if [[ -n "$(git -C "$SRC_DIR" status --porcelain)" ]]; then
  echo "external/projectm has local changes; refusing to apply $PATCH" >&2
  exit 1
fi
git -C "$SRC_DIR" apply "$PATCH"
trap 'git -C "$SRC_DIR" apply -R "$PATCH" || echo "warning: failed to revert $PATCH" >&2' EXIT

cmake -S "$SRC_DIR" -B "$BUILD_DIR" "${CMAKE_ARGS[@]}"
cmake --build "$BUILD_DIR" --parallel

rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

# The main libprojectM-4.a is self-contained: CMake folds the vendored
# subprojects (glad, projectM-eval, renderer, MilkdropPreset, ...) into it via
# $<TARGET_OBJECTS:...>. The sibling libglad.a / libprojectM_eval.a are the
# standalone target builds of those same objects, so merging them would produce
# duplicate symbols. Ship the main archive as-is.
MAIN_LIB="$(find "$BUILD_DIR" -name 'libprojectM-4.a' | head -1)"
[[ -n "$MAIN_LIB" ]] || { echo "libprojectM-4.a not found in $BUILD_DIR" >&2; exit 1; }
cp "$MAIN_LIB" "$OUT_LIB"

# Verify the entrypoint is present AND a real external symbol (T, not private).
# grep must consume all of nm's output (no -q short-circuit) or pipefail trips
# on the SIGPIPE nm gets when grep exits early.
nm "$OUT_LIB" 2>/dev/null | grep -E '\bT _projectm_create$' >/dev/null \
  || { echo "ERROR: _projectm_create not an exported symbol in $OUT_LIB" >&2; exit 1; }

echo "native artifact for $RID staged:"
ls -la "$OUT_LIB"
