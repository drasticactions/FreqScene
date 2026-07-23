#!/usr/bin/env bash
# Build FreqScene.Tui into a portable, self-contained NativeAOT package (tar.gz).
#
# Unlike the desktop head this ships no native libprojectM: the TUI never creates
# a ProjectM instance (it starts with rendering stopped and attaches no
# IVisualizerHost), so the DllImport resolver in NativeLoader is never hit. The
# only native asset in the package is the OpenAL-Soft runtime that
# Silk.NET.OpenAL.Soft.Native brings in for CaptureAudioSource.
#
# Pipeline:
#   1. `dotnet publish -p:PublishAot=true` self-contained per RID.
#   2. macOS universal only: merge the two publish trees into one directory,
#      lipo'ing every Mach-O (the AOT executable and the OpenAL dylib).
#   3. macOS: ad-hoc codesign every Mach-O. A lipo'd binary carries an invalid
#      signature and will not execute on Apple silicon until it is re-signed.
#   4. Emit FreqScene.Tui-<version>-<arch>.tar.gz.
#
# Usage:
#   eng/pack/build-tui.sh [options]
#
# Options:
#   --os <macos|linux>            Target OS (default: the host)
#   --arch <universal|arm64|x64>  Architecture. macOS: universal|arm64|x64
#                                   (default: universal). Linux: x64|arm64
#                                   (default: x64); universal is macOS-only.
#   --version <X.Y.Z>             Version used in the archive name (default: 1.0.0)
#   --output <dir>                Output directory (default: artifacts/pack)
#   -h, --help                    Show this help
set -euo pipefail

# ---------------------------------------------------------------------------
# Defaults & argument parsing
# ---------------------------------------------------------------------------
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

APP_NAME="FreqScene.Tui"             # assembly name == apphost/AOT binary name
PROJECT="$REPO_ROOT/src/FreqScene.Tui/FreqScene.Tui.csproj"
APP_VERSION="1.0.0"
OUTPUT_DIR="$REPO_ROOT/artifacts/pack"
TARGET_OS=""
ARCH=""

die() { echo "error: $*" >&2; exit 1; }
info() { echo "==> $*"; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --os)      TARGET_OS="${2:?}"; shift 2 ;;
    --arch)    ARCH="${2:?}"; shift 2 ;;
    --version) APP_VERSION="${2:?}"; shift 2 ;;
    --output)  OUTPUT_DIR="${2:?}"; shift 2 ;;
    -h|--help) sed -n '2,27p' "$0"; exit 0 ;;
    *) die "unknown option: $1 (see --help)" ;;
  esac
done

if [[ -z "$TARGET_OS" ]]; then
  case "$(uname -s)" in
    Darwin) TARGET_OS=macos ;;
    Linux)  TARGET_OS=linux ;;
    *) die "unsupported host $(uname -s); pass --os macos|linux (Windows uses build-tui.ps1)" ;;
  esac
fi
case "$TARGET_OS" in macos|linux) ;; *) die "--os must be macos|linux" ;; esac

# Cross-OS publishing is not supported here: the AOT link step needs the host's
# native toolchain (clang + the platform SDK) for the target OS.
case "$TARGET_OS" in
  macos) [[ "$(uname -s)" == "Darwin" ]] || die "--os macos must run on macOS" ;;
  linux) [[ "$(uname -s)" == "Linux" ]]  || die "--os linux must run on Linux" ;;
esac

# Resolve the RID set and the arch label used in the archive name.
if [[ "$TARGET_OS" == "macos" ]]; then
  ARCH="${ARCH:-universal}"
  case "$ARCH" in
    universal) RIDS=(osx-arm64 osx-x64) ;;
    arm64)     RIDS=(osx-arm64) ;;
    x64)       RIDS=(osx-x64) ;;
    *) die "--arch must be universal|arm64|x64 on macOS" ;;
  esac
else
  ARCH="${ARCH:-x64}"
  case "$ARCH" in
    x64)   RIDS=(linux-x64) ;;
    arm64) RIDS=(linux-arm64) ;;
    universal) die "--arch universal is macOS-only" ;;
    *) die "--arch must be x64|arm64 on Linux" ;;
  esac
fi

for tool in dotnet tar find file; do
  command -v "$tool" >/dev/null || die "required tool not found on PATH: $tool"
done
if [[ "$TARGET_OS" == "macos" ]]; then
  for tool in lipo codesign; do
    command -v "$tool" >/dev/null || die "required tool not found on PATH: $tool"
  done
fi

# ---------------------------------------------------------------------------
# 1. dotnet publish (NativeAOT, self-contained) per RID
# ---------------------------------------------------------------------------
WORK_DIR="$REPO_ROOT/artifacts/pack-work-tui-$TARGET_OS"
rm -rf "$WORK_DIR"
mkdir -p "$WORK_DIR"

pubdir() { echo "$WORK_DIR/publish/$1"; }

for rid in "${RIDS[@]}"; do
  info "dotnet publish $rid (NativeAOT)"
  dotnet publish "$PROJECT" \
    -c Release \
    -r "$rid" \
    --self-contained true \
    -p:PublishAot=true \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    -o "$(pubdir "$rid")"
done

# ---------------------------------------------------------------------------
# 2. Assemble the package directory (lipo Mach-O when building universal)
# ---------------------------------------------------------------------------
PKG_DIR="$WORK_DIR/$APP_NAME"
rm -rf "$PKG_DIR"
mkdir -p "$PKG_DIR"

is_macho() { file -b "$1" 2>/dev/null | grep -q 'Mach-O'; }
lipo_archs() { lipo -archs "$1" 2>/dev/null; }
has_arch() { case " $(lipo_archs "$1") " in *" $2 "*) return 0 ;; *) return 1 ;; esac; }

# Architectures a universal package must contain, derived from the RIDs.
WANT_ARCHS=()
for rid in "${RIDS[@]}"; do
  case "$rid" in
    osx-arm64) WANT_ARCHS+=(arm64) ;;
    osx-x64)   WANT_ARCHS+=(x86_64) ;;
  esac
done

# Merge one Mach-O across RID copies into $dest.
#  - If any copy already contains every wanted arch (NuGet ships some fat
#    dylibs, e.g. the OpenAL-Soft runtime), use it verbatim — lipo refuses
#    overlapping inputs.
#  - Otherwise take one slice per wanted arch and lipo the complementary set.
merge_macho() {
  local dest="$1"; shift
  local files=("$@")
  local f arch

  for f in "${files[@]}"; do
    local ok=1
    for arch in "${WANT_ARCHS[@]}"; do has_arch "$f" "$arch" || { ok=0; break; }; done
    [[ $ok -eq 1 ]] && { cp -c "$f" "$dest"; return; }
  done

  local slices=()
  for arch in "${WANT_ARCHS[@]}"; do
    for f in "${files[@]}"; do
      has_arch "$f" "$arch" || continue
      if [[ "$(lipo_archs "$f")" == "$arch" ]]; then
        slices+=("$f")
      else
        lipo "$f" -thin "$arch" -output "$dest.$arch.slice"
        slices+=("$dest.$arch.slice")
      fi
      break
    done
  done

  if [[ ${#slices[@]} -eq 0 ]]; then
    cp -c "${files[0]}" "$dest"
  elif [[ ${#slices[@]} -eq 1 ]]; then
    cp -c "${slices[0]}" "$dest"
  else
    lipo -create "${slices[@]}" -output "$dest"
  fi
  for f in "${slices[@]}"; do case "$f" in "$dest".*.slice) rm -f "$f" ;; esac; done
}

BASE_RID="${RIDS[0]}"
BASE="$(pubdir "$BASE_RID")"

# NativeAOT emits debug symbols next to the binary regardless of DebugType=none
# (a .dSYM bundle on macOS, a .dbg on Linux) — on macOS that is twice the size of
# everything else combined. Keep them out of the package entirely rather than
# copying and lipo'ing them first.
publish_files() { find "$1" -type f -not -path '*.dSYM/*' -not -name '*.dbg' -print0; }

info "assembling $APP_NAME ($ARCH)"
while IFS= read -r -d '' src; do
  rel="${src#"$BASE"/}"
  dest="$PKG_DIR/$rel"
  mkdir -p "$(dirname "$dest")"

  if [[ ${#RIDS[@]} -gt 1 ]] && is_macho "$src"; then
    inputs=("$src")
    for rid in "${RIDS[@]:1}"; do
      other="$(pubdir "$rid")/$rel"
      [[ -f "$other" ]] && is_macho "$other" && inputs+=("$other")
    done
    merge_macho "$dest" "${inputs[@]}"
  else
    cp "$src" "$dest"
  fi
done < <(publish_files "$BASE")

# Pull in files that exist only in a non-base RID (rare, but safe).
for rid in "${RIDS[@]:1}"; do
  rid_pub="$(pubdir "$rid")"
  while IFS= read -r -d '' src; do
    rel="${src#"$rid_pub"/}"
    dest="$PKG_DIR/$rel"
    [[ -e "$dest" ]] && continue
    mkdir -p "$(dirname "$dest")"
    cp "$src" "$dest"
  done < <(publish_files "$rid_pub")
done

EXE="$PKG_DIR/$APP_NAME"
[[ -f "$EXE" ]] || die "AOT binary '$APP_NAME' not found in publish output"
chmod +x "$EXE"

# ---------------------------------------------------------------------------
# 3. Ad-hoc sign (macOS). Mandatory: lipo invalidates the signature the linker
#    produced, and Apple silicon refuses to exec an incorrectly-signed binary.
# ---------------------------------------------------------------------------
if [[ "$TARGET_OS" == "macos" ]]; then
  info "ad-hoc signing Mach-O files"
  while IFS= read -r -d '' f; do
    is_macho "$f" || continue
    codesign --force --sign - "$f"
  done < <(find "$PKG_DIR" -type f -print0)
  codesign --verify --strict --verbose=2 "$EXE" || die "codesign verification failed"
fi

# ---------------------------------------------------------------------------
# 4. Emit the tarball
# ---------------------------------------------------------------------------
mkdir -p "$OUTPUT_DIR"
TARBALL="$OUTPUT_DIR/${APP_NAME}-${APP_VERSION}-${ARCH}.tar.gz"
rm -f "$TARBALL"
info "creating $TARBALL"
# COPYFILE_DISABLE keeps bsdtar from emitting ._AppleDouble members for the
# extended attributes codesign leaves behind.
COPYFILE_DISABLE=1 tar -czf "$TARBALL" -C "$WORK_DIR" "$APP_NAME"

info "done"
echo
echo "  package: $PKG_DIR"
echo "  tarball: $TARBALL"
echo "  size:    $(du -h "$TARBALL" | cut -f1)"
if [[ "$TARGET_OS" == "macos" ]]; then
  echo "  arch:    $(lipo -archs "$EXE")"
else
  echo "  arch:    $ARCH"
fi
