#!/usr/bin/env bash
# Build FreqScene.Cli (freqscene-cli) into a portable, self-contained NativeAOT
# package (tar.gz). Linux-only: the CLI renders headless via FreqScene.Linux
# (Wayland or DRM/KMS) and has no Windows/macOS counterpart.
#
# Unlike the TUI, the CLI DOES create a ProjectM instance, so the package ships
# the native libprojectM libraries (copied into runtimes/<rid>/native by the
# csproj). The OpenAL-Soft runtime that Silk.NET.OpenAL.Soft.Native brings in for
# --audio capture is also included in the publish tree.
#
# Pipeline:
#   1. Ensure native libprojectM exists for the target RID (auto-builds a missing
#      linux-x64 via eng/native/build-native.sh; other RIDs must be pre-built).
#   2. `dotnet publish -p:FreqSceneAot=true` self-contained for the RID.
#   3. Assemble a package dir: the publish tree plus a README.txt and a systemd
#      unit template for kiosk deployments.
#   4. Emit freqscene-cli-<version>-x64.tar.gz.
#
# Usage:
#   eng/pack/build-cli.sh [options]
#
# Options:
#   --version <X.Y.Z>    Version used in the archive name (default: 1.0.0)
#   --arch <x64|arm64>   Target architecture (default: x64). arm64 requires the
#                        native libprojectM for linux-arm64 to already exist under
#                        artifacts/native/ — this script does not cross-build it.
#   --output <dir>       Output directory (default: artifacts/pack)
#   -h, --help           Show this help
set -euo pipefail

# ---------------------------------------------------------------------------
# Defaults & argument parsing
# ---------------------------------------------------------------------------
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

BIN_NAME="freqscene-cli"             # AssemblyName == apphost/AOT binary name
PKG_NAME="freqscene-cli"             # package dir + archive basename
PROJECT="$REPO_ROOT/src/FreqScene.Cli/FreqScene.Cli.csproj"
README_SRC="$REPO_ROOT/src/FreqScene.Cli/README.txt"
UNIT_SRC="$REPO_ROOT/src/FreqScene.Cli/$BIN_NAME.service"
APP_VERSION="1.0.0"
ARCH="x64"
OUTPUT_DIR="$REPO_ROOT/artifacts/pack"

die() { echo "error: $*" >&2; exit 1; }
info() { echo "==> $*"; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) APP_VERSION="${2:?}"; shift 2 ;;
    --arch)    ARCH="${2:?}"; shift 2 ;;
    --output)  OUTPUT_DIR="${2:?}"; shift 2 ;;
    -h|--help) sed -n '2,29p' "$0"; exit 0 ;;
    *) die "unknown option: $1 (see --help)" ;;
  esac
done

[[ "$(uname -s)" == "Linux" ]] || die "this script must run on Linux (freqscene-cli is Linux-only)"
case "$ARCH" in x64|arm64) ;; *) die "--arch must be x64|arm64" ;; esac

# Map the friendly arch to a .NET RID.
case "$ARCH" in
  x64)   RID="linux-x64" ;;
  arm64) RID="linux-arm64" ;;
esac

for tool in dotnet tar find; do
  command -v "$tool" >/dev/null || die "required tool not found on PATH: $tool"
done

[[ -f "$README_SRC" ]] || die "README not found at $README_SRC"
[[ -f "$UNIT_SRC" ]] || die "systemd unit not found at $UNIT_SRC"

# ---------------------------------------------------------------------------
# 1. Native libprojectM for the RID (auto-build linux-x64 if missing)
# ---------------------------------------------------------------------------
native_dir="$REPO_ROOT/artifacts/native/$RID"
if [[ ! -f "$native_dir/libprojectM-4.so.4" ]]; then
  if [[ "$RID" == "linux-x64" ]]; then
    info "native libprojectM for $RID missing — building (eng/native/build-native.sh $RID)"
    "$REPO_ROOT/eng/native/build-native.sh" "$RID"
  else
    die "native libprojectM for $RID not found at $native_dir (build-native.sh only cross-builds linux-x64)"
  fi
else
  info "native libprojectM for $RID present"
fi

# ---------------------------------------------------------------------------
# 2. dotnet publish (NativeAOT, self-contained)
# ---------------------------------------------------------------------------
WORK_DIR="$REPO_ROOT/artifacts/pack-work-cli"
rm -rf "$WORK_DIR"
mkdir -p "$WORK_DIR"

PUBLISH_DIR="$WORK_DIR/publish"

info "dotnet publish $RID (NativeAOT)"
dotnet publish "$PROJECT" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:FreqSceneAot=true \
  -p:DebugType=none \
  -p:DebugSymbols=false \
  -o "$PUBLISH_DIR"

[[ -f "$PUBLISH_DIR/$BIN_NAME" ]] || die "AOT binary '$BIN_NAME' not found in publish output"

# ---------------------------------------------------------------------------
# 3. Assemble the package directory
# ---------------------------------------------------------------------------
PKG_DIR="$WORK_DIR/$PKG_NAME"
rm -rf "$PKG_DIR"
mkdir -p "$PKG_DIR"

info "assembling $PKG_NAME ($ARCH)"
# NativeAOT emits a .dbg debug file next to the binary regardless of
# DebugType=none — keep it out of the package.
while IFS= read -r -d '' src; do
  rel="${src#"$PUBLISH_DIR"/}"
  dest="$PKG_DIR/$rel"
  mkdir -p "$(dirname "$dest")"
  cp "$src" "$dest"
done < <(find "$PUBLISH_DIR" -type f -not -name '*.dbg' -print0)

chmod +x "$PKG_DIR/$BIN_NAME"

cp "$README_SRC" "$PKG_DIR/README.txt"
cp "$UNIT_SRC" "$PKG_DIR/$BIN_NAME.service"

# ---------------------------------------------------------------------------
# 4. Emit the tarball
# ---------------------------------------------------------------------------
mkdir -p "$OUTPUT_DIR"
TARBALL="$OUTPUT_DIR/${PKG_NAME}-${APP_VERSION}-${ARCH}.tar.gz"
rm -f "$TARBALL"
info "creating $TARBALL"
tar -czf "$TARBALL" -C "$WORK_DIR" "$PKG_NAME"

info "done"
echo
echo "  package: $PKG_DIR"
echo "  tarball: $TARBALL"
echo "  size:    $(du -h "$TARBALL" | cut -f1)"
echo "  arch:    $ARCH"
