#!/usr/bin/env bash
# Build FreqScene into a portable Linux AppImage.
#
# Pipeline:
#   1. Ensure native libprojectM exists for the target RID (auto-builds a missing
#      linux-x64 via eng/native/build-native.sh; other RIDs must be pre-built).
#   2. `dotnet publish` self-contained + trimmed for the RID.
#   3. Assemble an AppDir (publish tree under usr/bin, plus AppRun, .desktop, icon).
#   4. Pack it with appimagetool into a single-file .AppImage, embedding update
#      information so a matching .zsync delta-update file is emitted alongside it.
#
# Usage:
#   eng/pack/build-linux-appimage.sh [options]
#
# Options:
#   --version <X.Y.Z>    Marketing version (default: 1.0.0)
#   --arch <x64|arm64>   Target architecture (default: x64). arm64 requires the
#                        native libprojectM for linux-arm64 to already exist under
#                        artifacts/native/ — this script does not cross-build it.
#   --no-trim            Publish self-contained but without trimming
#   --output <dir>       Output directory (default: artifacts/pack)
#   --appimagetool <p>   Path to appimagetool (default: found on PATH, else fetched)
#   --update-info <str>  Raw AppImage update-information string to embed verbatim
#                        (e.g. "zsync|https://host/FreqScene-latest-x86_64.AppImage.zsync").
#                        Overrides --github and remote auto-derivation.
#   --github <owner/repo> Build a gh-releases-zsync update string for this repo
#                        (release tag "latest"). Defaults to the origin remote.
#   --no-update-info     Do not embed update information / emit a .zsync file
#   -h, --help           Show this help
set -euo pipefail

# ---------------------------------------------------------------------------
# Defaults & argument parsing
# ---------------------------------------------------------------------------
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

APP_VERSION="1.0.0"
ARCH="x64"
TRIM=1
OUTPUT_DIR="$REPO_ROOT/artifacts/pack"
APPIMAGETOOL="${APPIMAGETOOL:-}"
UPDATE_INFO=""                       # raw string (--update-info); wins if set
GITHUB_REPO=""                       # owner/repo (--github); else derived from origin
UPDATE_ENABLED=1                     # --no-update-info clears this

APP_NAME="FreqScene"                 # display name + apphost/assembly name
PROJECT="$REPO_ROOT/src/FreqScene/FreqScene.csproj"
ICON_SRC="$REPO_ROOT/eng/pack/assets/FreqScene.png"
BUNDLE_ID="com.drasticactions.freqscene"

die() { echo "error: $*" >&2; exit 1; }
info() { echo "==> $*"; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)      APP_VERSION="${2:?}"; shift 2 ;;
    --arch)         ARCH="${2:?}"; shift 2 ;;
    --no-trim)      TRIM=0; shift ;;
    --output)       OUTPUT_DIR="${2:?}"; shift 2 ;;
    --appimagetool) APPIMAGETOOL="${2:?}"; shift 2 ;;
    --update-info)  UPDATE_INFO="${2:?}"; shift 2 ;;
    --github)       GITHUB_REPO="${2:?}"; shift 2 ;;
    --no-update-info) UPDATE_ENABLED=0; shift ;;
    -h|--help)      sed -n '2,37p' "$0"; exit 0 ;;
    *) die "unknown option: $1 (see --help)" ;;
  esac
done

[[ "$(uname -s)" == "Linux" ]] || die "this script must run on Linux"
case "$ARCH" in x64|arm64) ;; *) die "--arch must be x64|arm64" ;; esac

# Map the friendly arch to a .NET RID and the AppImage/uname machine name.
case "$ARCH" in
  x64)   RID="linux-x64";   IMG_ARCH="x86_64" ;;
  arm64) RID="linux-arm64"; IMG_ARCH="aarch64" ;;
esac

for tool in dotnet file find; do
  command -v "$tool" >/dev/null || die "required tool not found on PATH: $tool"
done

[[ -f "$ICON_SRC" ]] || die "icon not found at $ICON_SRC"

# ---------------------------------------------------------------------------
# Update information (feeds appimagetool -u; makes it emit the .zsync file).
#   Precedence: --update-info (raw) > --github owner/repo > origin remote.
# The gh-releases-zsync filename glob is version-agnostic so a "latest" release
# updates any installed version.
# ---------------------------------------------------------------------------
derive_github_repo() {
  local url; url="$(git -C "$REPO_ROOT" config --get remote.origin.url 2>/dev/null || true)"
  [[ -n "$url" ]] || return 1
  url="${url%.git}"
  case "$url" in
    *@*:*/*)                 echo "${url##*:}" ;;                       # git@github.com:owner/repo
    ssh://*|https://*|http://*) echo "${url#*://}" | sed -E 's#^[^/]+/##' ;; # host/owner/repo
    *) return 1 ;;
  esac
}

if [[ $UPDATE_ENABLED -eq 1 && -z "$UPDATE_INFO" ]]; then
  [[ -n "$GITHUB_REPO" ]] || GITHUB_REPO="$(derive_github_repo || true)"
  if [[ -n "$GITHUB_REPO" && "$GITHUB_REPO" == */* ]]; then
    gh_owner="${GITHUB_REPO%%/*}"
    gh_name="${GITHUB_REPO##*/}"
    UPDATE_INFO="gh-releases-zsync|${gh_owner}|${gh_name}|latest|${APP_NAME}-*-${IMG_ARCH}.AppImage.zsync"
  else
    info "no --github/--update-info and no usable origin remote — skipping update information"
    UPDATE_ENABLED=0
  fi
fi
[[ $UPDATE_ENABLED -eq 1 && -n "$UPDATE_INFO" ]] && info "update information: $UPDATE_INFO"

# ---------------------------------------------------------------------------
# Resolve appimagetool (PATH → explicit → download a pinned continuous build)
# ---------------------------------------------------------------------------
if [[ -z "$APPIMAGETOOL" ]]; then
  if command -v appimagetool >/dev/null; then
    APPIMAGETOOL="$(command -v appimagetool)"
  else
    cache="$REPO_ROOT/artifacts/tools"
    APPIMAGETOOL="$cache/appimagetool-x86_64.AppImage"
    if [[ ! -x "$APPIMAGETOOL" ]]; then
      info "appimagetool not on PATH — downloading to $APPIMAGETOOL"
      mkdir -p "$cache"
      url="https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"
      if command -v curl >/dev/null; then
        curl -fL "$url" -o "$APPIMAGETOOL"
      elif command -v wget >/dev/null; then
        wget -O "$APPIMAGETOOL" "$url"
      else
        die "need curl or wget to fetch appimagetool, or pass --appimagetool <path>"
      fi
      chmod +x "$APPIMAGETOOL"
    fi
  fi
fi
info "appimagetool: $APPIMAGETOOL"

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
# 2. dotnet publish (self-contained, trimmed)
# ---------------------------------------------------------------------------
WORK_DIR="$REPO_ROOT/artifacts/pack-work-linux"
rm -rf "$WORK_DIR"
mkdir -p "$WORK_DIR"

PUBLISH_DIR="$WORK_DIR/publish"
info "dotnet publish $RID (self-contained, trim=$TRIM)"
dotnet publish "$PROJECT" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:PublishTrimmed="$([[ $TRIM -eq 1 ]] && echo true || echo false)" \
  -p:PublishSingleFile=false \
  -p:DebugType=none \
  -p:DebugSymbols=false \
  -o "$PUBLISH_DIR"

[[ -f "$PUBLISH_DIR/$APP_NAME" ]] || die "apphost '$APP_NAME' not found in publish output"

# ---------------------------------------------------------------------------
# 3. Assemble the AppDir
# ---------------------------------------------------------------------------
APPDIR="$WORK_DIR/$APP_NAME.AppDir"
rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin"

info "assembling $APP_NAME.AppDir ($IMG_ARCH)"
cp -a "$PUBLISH_DIR/." "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/$APP_NAME"

# Icon: AppDir root (named after the desktop Icon key) + the freedesktop hicolor
# path so desktop environments can theme it once the AppImage is integrated.
cp "$ICON_SRC" "$APPDIR/$APP_NAME.png"
ICON_INSTALL="$APPDIR/usr/share/icons/hicolor/256x256/apps"
mkdir -p "$ICON_INSTALL"
cp "$ICON_SRC" "$ICON_INSTALL/$APP_NAME.png"

# .desktop entry (AppDir root + the canonical applications path).
DESKTOP_REL="$APP_NAME.desktop"
cat > "$APPDIR/$DESKTOP_REL" <<DESKTOP
[Desktop Entry]
Type=Application
Name=$APP_NAME
Comment=Milkdrop-style music visualizer
Exec=$APP_NAME
Icon=$APP_NAME
Categories=AudioVideo;Audio;Player;
Terminal=false
StartupWMClass=$APP_NAME
X-AppImage-Version=$APP_VERSION
DESKTOP
mkdir -p "$APPDIR/usr/share/applications"
cp "$APPDIR/$DESKTOP_REL" "$APPDIR/usr/share/applications/$DESKTOP_REL"

if command -v desktop-file-validate >/dev/null; then
  desktop-file-validate "$APPDIR/$DESKTOP_REL" || die ".desktop failed validation"
fi

# AppRun launcher: resolve our own dir, prefer bundled native libs, then exec.
# $RID/$APP_NAME are expanded now; the runtime shell vars are escaped to stay literal.
cat > "$APPDIR/AppRun" <<APPRUN
#!/bin/sh
HERE="\$(dirname "\$(readlink -f "\$0")")"
export LD_LIBRARY_PATH="\$HERE/usr/bin:\$HERE/usr/bin/runtimes/$RID/native:\${LD_LIBRARY_PATH:-}"
export DOTNET_ROOT="\$HERE/usr/bin"
exec "\$HERE/usr/bin/$APP_NAME" "\$@"
APPRUN
chmod +x "$APPDIR/AppRun"

# ---------------------------------------------------------------------------
# 4. Pack the AppImage
# ---------------------------------------------------------------------------
mkdir -p "$OUTPUT_DIR"
IMG="$OUTPUT_DIR/${APP_NAME}-${APP_VERSION}-${IMG_ARCH}.AppImage"
ZSYNC="$IMG.zsync"
rm -f "$IMG" "$ZSYNC"

# appimagetool reads ARCH for anything it can't infer from the AppDir contents.
# --appimage-extract-and-run avoids needing FUSE on the build host. With -u it
# embeds the update string and (given a zsync backend) writes $IMG.zsync.
IMG_ARGS=(--appimage-extract-and-run --no-appstream)
[[ -n "$UPDATE_INFO" ]] && IMG_ARGS+=(-u "$UPDATE_INFO")

info "packing $IMG"
# appimagetool writes the .zsync to its CWD by basename, so run it from the
# output dir (paths are absolute) to keep the image and its .zsync together.
( cd "$OUTPUT_DIR" && ARCH="$IMG_ARCH" "$APPIMAGETOOL" "${IMG_ARGS[@]}" "$APPDIR" "$IMG" )

if [[ -n "$UPDATE_INFO" && ! -f "$ZSYNC" ]]; then
  info "warning: update info was embedded but no .zsync was produced —" \
       "install zsyncmake (or use an appimagetool with a bundled zsync backend)"
fi

info "done"
echo
echo "  appdir: $APPDIR"
echo "  image:  $IMG"
[[ -f "$ZSYNC" ]] && echo "  zsync:  $ZSYNC"
[[ -n "$UPDATE_INFO" ]] && echo "  update: $UPDATE_INFO"
echo "  arch:   $IMG_ARCH"
echo "  trim:   $([[ $TRIM -eq 1 ]] && echo on || echo off)"
