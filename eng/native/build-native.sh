#!/usr/bin/env bash
# Usage: eng/native/build-native.sh <rid>
#   rid: osx-arm64 | osx-x64 | linux-x64 | android-arm64 | android-x64
# Android needs an NDK: set ANDROID_NDK_HOME/ANDROID_NDK_ROOT, or have one
# installed under the default SDK location (the newest is picked). The Android
# build transiently applies eng/native/patches/android.patch to the submodule
# (reverted after the build, even on failure).
set -euo pipefail

RID="${1:-}"
[[ -n "$RID" ]] || { echo "usage: $0 <osx-arm64|osx-x64|linux-x64|android-arm64|android-x64>" >&2; exit 2; }

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

find_ndk() {
  local ndk="${ANDROID_NDK_HOME:-${ANDROID_NDK_ROOT:-}}"
  if [[ -z "$ndk" ]]; then
    local sdk
    for sdk in "${ANDROID_HOME:-}" "$HOME/Library/Android/sdk" "$HOME/Android/Sdk" /usr/local/lib/android/sdk; do
      if [[ -n "$sdk" && -d "$sdk/ndk" ]]; then
        ndk="$sdk/ndk/$(ls "$sdk/ndk" | sort -V | tail -1)"
        break
      fi
    done
  fi
  [[ -f "$ndk/build/cmake/android.toolchain.cmake" ]] ||
    { echo "Android NDK not found (set ANDROID_NDK_HOME)" >&2; exit 1; }
  echo "$ndk"
}

# Android: cross-compile with the NDK toolchain. CMAKE_SYSTEM_NAME=Android
# (set by the toolchain file) forces projectM's ENABLE_GLES on. c++_shared
# because we ship two C++ shared libraries — the static STL is unsupported by
# the NDK in that configuration; libc++_shared.so ships alongside.
android_args() {
  NDK="$(find_ndk)"
  CMAKE_ARGS+=(
    "-DCMAKE_TOOLCHAIN_FILE=$NDK/build/cmake/android.toolchain.cmake"
    -DANDROID_ABI="$1"
    -DANDROID_PLATFORM=android-24
    -DANDROID_STL=c++_shared
    # Android 15+ devices can use 16 KB memory pages; NDK r27 still links
    # 4 KB-aligned by default.
    -DCMAKE_SHARED_LINKER_FLAGS=-Wl,-z,max-page-size=16384
  )
}

case "$RID" in
  osx-arm64) CMAKE_ARGS+=(-DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0) ;;
  osx-x64)   CMAKE_ARGS+=(-DCMAKE_OSX_ARCHITECTURES=x86_64 -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0) ;;
  linux-x64) ;;
  android-arm64) android_args arm64-v8a ;;
  android-x64)   android_args x86_64 ;;
  *) echo "unsupported rid: $RID" >&2; exit 2 ;;
esac

git -C "$REPO_ROOT" submodule update --init --recursive external/projectm

if [[ "$RID" == android-* ]]; then
  # Android emulators (and some GPUs) cap OpenGL ES at 3.0; libprojectM's probe
  # demands 3.2 but the renderer itself runs on ES 3.0/ESSL 3.00 (same
  # relaxation apple.patch carries for iOS/tvOS). Apply transiently and always
  # revert so the submodule stays clean for git.
  ANDROID_PATCH="$REPO_ROOT/eng/native/patches/android.patch"
  if [[ -n "$(git -C "$SRC_DIR" status --porcelain)" ]]; then
    echo "external/projectm has local changes; refusing to apply $ANDROID_PATCH" >&2
    exit 1
  fi
  git -C "$SRC_DIR" apply "$ANDROID_PATCH"
  trap 'git -C "$SRC_DIR" apply -R "$ANDROID_PATCH" || echo "warning: failed to revert $ANDROID_PATCH from external/projectm" >&2' EXIT
fi

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
elif [[ "$RID" == android-* ]]; then
  LIBDIR="$STAGE_DIR/lib"
  [[ -d "$LIBDIR" ]] || LIBDIR="$STAGE_DIR/lib64"

  # Android has no versioned sonames — the toolchain already produced flat
  # names, and the app's native-lib directory acts as the search path, so no
  # rpath/soname fixups are needed.
  cp -L "$LIBDIR/libprojectM-4.so" "$OUT_DIR/libprojectM-4.so"
  cp -L "$LIBDIR/libprojectM-4-playlist.so" "$OUT_DIR/libprojectM-4-playlist.so"

  case "$RID" in
    android-arm64) TRIPLE=aarch64-linux-android ;;
    android-x64)   TRIPLE=x86_64-linux-android ;;
  esac
  HOST_TAG=$([[ "$(uname -s)" == Darwin ]] && echo darwin-x86_64 || echo linux-x86_64)
  TOOLBIN="$NDK/toolchains/llvm/prebuilt/$HOST_TAG/bin"
  cp -L "$NDK/toolchains/llvm/prebuilt/$HOST_TAG/sysroot/usr/lib/$TRIPLE/libc++_shared.so" \
    "$OUT_DIR/libc++_shared.so"

  "$TOOLBIN/llvm-nm" -D --defined-only "$OUT_DIR/libprojectM-4.so" | grep -q ' projectm_create'
  "$TOOLBIN/llvm-nm" -D --defined-only "$OUT_DIR/libprojectM-4-playlist.so" | grep -q ' projectm_playlist_create'
  "$TOOLBIN/llvm-readelf" -d "$OUT_DIR/libprojectM-4-playlist.so" | grep -q 'libprojectM-4\.so'
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
