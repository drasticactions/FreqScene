#!/usr/bin/env bash
# Build FreqScene into a macOS .app bundle (universal arm64 + x86_64) and zip it.
#
# Pipeline:
#   1. Ensure native libprojectM exists for osx-arm64 and osx-x64 (auto-builds any
#      missing RID via eng/native/build-native.sh).
#   2. `dotnet publish` self-contained + trimmed for each RID.
#   3. Merge the two publish trees into one universal .app (lipo every Mach-O,
#      copy arch-independent managed assemblies once).
#   4. Write Info.plist + entitlements, generate AppIcon.icns from the PNG asset.
#   5. Sign per the chosen flow, optionally notarize + staple.
#   6. Emit a .zip.
#
# Usage:
#   eng/pack/build-macos-app.sh [options]
#
# Options:
#   --sign <adhoc|devid|notarize>  Signing flow (default: adhoc)
#   --version <X.Y.Z>              Marketing version (default: 1.0.0)
#   --bundle-id <id>              CFBundleIdentifier (default: com.drasticactions.freqscene)
#   --identity <name>             Codesign identity for devid/notarize
#                                   (or env CODESIGN_IDENTITY)
#   --notary-profile <name>       notarytool keychain profile for the notarize flow
#                                   (or env NOTARY_PROFILE). Create once with:
#                                   xcrun notarytool store-credentials <name> \
#                                     --apple-id <you@x> --team-id <TEAMID> --password <app-specific-pw>
#   --arch <universal|arm64|x64>  Bundle architecture (default: universal)
#   --aot                         Publish with NativeAOT (implies trimming; drops
#                                   the JIT/unsigned-exec entitlements). Mutually
#                                   exclusive with --no-trim.
#   --no-trim                     Publish self-contained but without trimming
#   --output <dir>                Output directory (default: artifacts/pack)
#   -h, --help                    Show this help
set -euo pipefail

# ---------------------------------------------------------------------------
# Defaults & argument parsing
# ---------------------------------------------------------------------------
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

SIGN_FLOW="adhoc"
APP_VERSION="1.0.0"
BUNDLE_ID="com.drasticactions.freqscene"
IDENTITY="${CODESIGN_IDENTITY:-}"
NOTARY_PROFILE="${NOTARY_PROFILE:-}"
ARCH="universal"
TRIM=1
AOT=0
OUTPUT_DIR="$REPO_ROOT/artifacts/pack"

APP_NAME="FreqScene"                 # display name + apphost/assembly name
PROJECT="$REPO_ROOT/src/FreqScene/FreqScene.csproj"
ICON_SRC="$REPO_ROOT/eng/pack/assets/FreqScene.png"
MIN_MACOS="11.0"

die() { echo "error: $*" >&2; exit 1; }
info() { echo "==> $*"; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --sign)           SIGN_FLOW="${2:?}"; shift 2 ;;
    --version)        APP_VERSION="${2:?}"; shift 2 ;;
    --bundle-id)      BUNDLE_ID="${2:?}"; shift 2 ;;
    --identity)       IDENTITY="${2:?}"; shift 2 ;;
    --notary-profile) NOTARY_PROFILE="${2:?}"; shift 2 ;;
    --arch)           ARCH="${2:?}"; shift 2 ;;
    --aot)            AOT=1; shift ;;
    --no-trim)        TRIM=0; shift ;;
    --output)         OUTPUT_DIR="${2:?}"; shift 2 ;;
    -h|--help)        sed -n '2,33p' "$0"; exit 0 ;;
    *) die "unknown option: $1 (see --help)" ;;
  esac
done

[[ "$(uname -s)" == "Darwin" ]] || die "this script must run on macOS"
case "$SIGN_FLOW" in adhoc|devid|notarize) ;; *) die "--sign must be adhoc|devid|notarize" ;; esac
case "$ARCH" in universal|arm64|x64) ;; *) die "--arch must be universal|arm64|x64" ;; esac
# NativeAOT always trims; the two flags contradict each other.
[[ $AOT -eq 1 && $TRIM -eq 0 ]] && die "--aot and --no-trim are mutually exclusive (AOT implies trimming)"

# Which RIDs do we need to publish?
case "$ARCH" in
  universal) RIDS=(osx-arm64 osx-x64) ;;
  arm64)     RIDS=(osx-arm64) ;;
  x64)       RIDS=(osx-x64) ;;
esac

if [[ "$SIGN_FLOW" != "adhoc" ]]; then
  [[ -n "$IDENTITY" ]] || die "--identity (or \$CODESIGN_IDENTITY) is required for the '$SIGN_FLOW' flow"
fi
if [[ "$SIGN_FLOW" == "notarize" ]]; then
  [[ -n "$NOTARY_PROFILE" ]] || die "--notary-profile (or \$NOTARY_PROFILE) is required for the 'notarize' flow"
fi

for tool in dotnet lipo codesign iconutil sips ditto plutil; do
  command -v "$tool" >/dev/null || die "required tool not found on PATH: $tool"
done

# ---------------------------------------------------------------------------
# 1. Native libprojectM per RID (auto-build if missing)
# ---------------------------------------------------------------------------
for rid in "${RIDS[@]}"; do
  native_dir="$REPO_ROOT/artifacts/native/$rid"
  if [[ ! -f "$native_dir/libprojectM-4.dylib" ]]; then
    info "native libprojectM for $rid missing — building (eng/native/build-native.sh $rid)"
    "$REPO_ROOT/eng/native/build-native.sh" "$rid"
  else
    info "native libprojectM for $rid present"
  fi
done

# ---------------------------------------------------------------------------
# 2. dotnet publish (self-contained, trimmed) per RID
# ---------------------------------------------------------------------------
WORK_DIR="$REPO_ROOT/artifacts/pack-work"
rm -rf "$WORK_DIR"
mkdir -p "$WORK_DIR"

pubdir() { echo "$WORK_DIR/publish/$1"; }

# NativeAOT implies self-contained + trimming and emits a single native Mach-O
# (all managed code compiled in); the trimmed path keeps the apphost + managed
# assemblies. PublishSingleFile is meaningless under AOT, so only set it otherwise.
publish_props() {
  if [[ $AOT -eq 1 ]]; then
    printf '%s\n' -p:PublishAot=true
  else
    printf '%s\n' \
      -p:PublishTrimmed="$([[ $TRIM -eq 1 ]] && echo true || echo false)" \
      -p:PublishSingleFile=false
  fi
}

for rid in "${RIDS[@]}"; do
  out="$(pubdir "$rid")"
  info "dotnet publish $rid ($([[ $AOT -eq 1 ]] && echo 'NativeAOT' || echo "self-contained, trim=$TRIM"))"
  # shellcheck disable=SC2046
  dotnet publish "$PROJECT" \
    -c Release \
    -r "$rid" \
    --self-contained true \
    $(publish_props) \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    -o "$out"
done

# ---------------------------------------------------------------------------
# 3. Assemble the .app (merge trees, lipo Mach-O)
# ---------------------------------------------------------------------------
APP="$WORK_DIR/$APP_NAME.app"
CONTENTS="$APP/Contents"
MACOS_DIR="$CONTENTS/MacOS"
RES_DIR="$CONTENTS/Resources"
rm -rf "$APP"
mkdir -p "$MACOS_DIR" "$RES_DIR"

is_macho() { file -b "$1" 2>/dev/null | grep -q 'Mach-O'; }
lipo_archs() { lipo -archs "$1" 2>/dev/null; }
has_arch() { case " $(lipo_archs "$1") " in *" $2 "*) return 0 ;; *) return 1 ;; esac; }

# Architectures the universal bundle must contain, derived from the RIDs.
WANT_ARCHS=()
for rid in "${RIDS[@]}"; do
  case "$rid" in
    osx-arm64) WANT_ARCHS+=(arm64) ;;
    osx-x64)   WANT_ARCHS+=(x86_64) ;;
  esac
done

# Merge one Mach-O across RID copies into $dest.
#  - If any copy already contains every wanted arch (NuGet ships fat dylibs like
#    libSkiaSharp/libHarfBuzzSharp), use it verbatim — lipo refuses overlaps.
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

info "assembling $APP_NAME.app ($ARCH)"
# Walk the base publish tree; mirror every file into Contents/MacOS.
# For Mach-O files present in every RID, lipo them into a universal binary.
while IFS= read -r -d '' src; do
  rel="${src#"$BASE"/}"
  dest="$MACOS_DIR/$rel"
  mkdir -p "$(dirname "$dest")"

  if [[ ${#RIDS[@]} -gt 1 ]] && is_macho "$src"; then
    inputs=("$src")
    for rid in "${RIDS[@]:1}"; do
      other="$(pubdir "$rid")/$rel"
      [[ -f "$other" ]] && is_macho "$other" && inputs+=("$other")
    done
    merge_macho "$dest" "${inputs[@]}"
  else
    cp -c "$src" "$dest"
  fi
done < <(find "$BASE" -type f -print0)

# Pull in any Mach-O files that exist only in a non-base RID (rare, but safe).
for rid in "${RIDS[@]:1}"; do
  rid_pub="$(pubdir "$rid")"
  while IFS= read -r -d '' src; do
    rel="${src#"$rid_pub"/}"
    dest="$MACOS_DIR/$rel"
    [[ -e "$dest" ]] && continue
    mkdir -p "$(dirname "$dest")"
    cp -c "$src" "$dest"
  done < <(find "$rid_pub" -type f -print0)
done

[[ -f "$MACOS_DIR/$APP_NAME" ]] || die "apphost '$APP_NAME' not found in publish output"
chmod +x "$MACOS_DIR/$APP_NAME"

# ---------------------------------------------------------------------------
# 4. Info.plist, icon, entitlements
# ---------------------------------------------------------------------------
# Generate a multi-resolution AppIcon.icns from the shared 1024x1024 PNG master
# (the same source Linux/Windows use), rather than shipping a prebuilt .icns.
# sips downscales the master into each iconset slot (16 through 512@2x=1024);
# iconutil packs them. The 1024 source feeds the largest Retina slot at native
# resolution, so no size is upscaled.
build_icns() {
  local src="$1" out="$2"
  local iconset; iconset="$(mktemp -d)/AppIcon.iconset"
  mkdir -p "$iconset"
  local sizes=(16 32 32 64 128 256 256 512 512 1024)
  local names=(
    icon_16x16.png icon_16x16@2x.png icon_32x32.png icon_32x32@2x.png
    icon_128x128.png icon_128x128@2x.png icon_256x256.png icon_256x256@2x.png
    icon_512x512.png icon_512x512@2x.png
  )
  local i
  for i in "${!sizes[@]}"; do
    sips -z "${sizes[$i]}" "${sizes[$i]}" "$src" --out "$iconset/${names[$i]}" >/dev/null
  done
  iconutil -c icns "$iconset" -o "$out"
  rm -rf "$(dirname "$iconset")"
}

if [[ -f "$ICON_SRC" ]]; then
  build_icns "$ICON_SRC" "$RES_DIR/AppIcon.icns"
  ICON_KEY="<key>CFBundleIconFile</key><string>AppIcon</string>"
else
  info "icon not found at $ICON_SRC — shipping without a custom icon"
  ICON_KEY=""
fi

cat > "$CONTENTS/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>$APP_NAME</string>
    <key>CFBundleDisplayName</key><string>$APP_NAME</string>
    <key>CFBundleExecutable</key><string>$APP_NAME</string>
    <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$APP_VERSION</string>
    <key>CFBundleVersion</key><string>$APP_VERSION</string>
    $ICON_KEY
    <key>LSMinimumSystemVersion</key><string>$MIN_MACOS</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSPrincipalClass</key><string>NSApplication</string>
    <key>NSSupportsAutomaticGraphicsSwitching</key><true/>
    <key>LSApplicationCategoryType</key><string>public.app-category.music</string>
    <key>NSMicrophoneUsageDescription</key><string>FreqScene captures audio to drive the music visualizer.</string>
</dict>
</plist>
PLIST
plutil -lint "$CONTENTS/Info.plist" >/dev/null

# Hardened-runtime entitlements: the CoreCLR JIT needs writable-exec memory, so
# the trimmed path relaxes JIT/unsigned-exec. NativeAOT has no JIT, so those are
# dropped for a tighter runtime. Library validation stays relaxed for the
# unsigned-at-build third-party dylibs; the visualizer always needs mic capture.
ENTITLEMENTS="$WORK_DIR/entitlements.plist"
{
  cat <<'ENT'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
ENT
  if [[ $AOT -eq 0 ]]; then
    cat <<'ENT'
    <key>com.apple.security.cs.allow-jit</key><true/>
    <key>com.apple.security.cs.allow-unsigned-executable-memory</key><true/>
ENT
  fi
  cat <<'ENT'
    <key>com.apple.security.cs.disable-library-validation</key><true/>
    <key>com.apple.security.device.audio-input</key><true/>
</dict>
</plist>
ENT
} > "$ENTITLEMENTS"

# ---------------------------------------------------------------------------
# 5. Signing
# ---------------------------------------------------------------------------
sign_inner_out() {
  # Sign every Mach-O inside the bundle, deepest first, then the bundle itself.
  local id="$1" opts=("${@:2}")
  # Deepest paths first so nested code is signed before its container.
  while IFS= read -r -d '' f; do
    is_macho "$f" || continue
    codesign --force --timestamp "${opts[@]}" --sign "$id" "$f"
  done < <(find "$MACOS_DIR" -type f -print0 | sort -zr)
  codesign --force --timestamp "${opts[@]}" --sign "$id" "$APP"
}

case "$SIGN_FLOW" in
  adhoc)
    info "ad-hoc signing (codesign -s -)"
    # Deep ad-hoc sign is fine for local/unsigned use.
    codesign --force --deep --sign - "$APP"
    ;;
  devid|notarize)
    info "signing with hardened runtime as: $IDENTITY"
    sign_inner_out "$IDENTITY" --options runtime --entitlements "$ENTITLEMENTS"
    ;;
esac

codesign --verify --deep --strict --verbose=2 "$APP" || die "codesign verification failed"

# ---------------------------------------------------------------------------
# 6. Notarize + staple (notarize flow only)
# ---------------------------------------------------------------------------
mkdir -p "$OUTPUT_DIR"
ZIP="$OUTPUT_DIR/${APP_NAME}-${APP_VERSION}-${ARCH}.zip"

if [[ "$SIGN_FLOW" == "notarize" ]]; then
  submit_zip="$WORK_DIR/notarize-submit.zip"
  info "zipping for notarization"
  ditto -c -k --keepParent "$APP" "$submit_zip"
  info "submitting to notarytool (profile: $NOTARY_PROFILE) — waiting..."
  xcrun notarytool submit "$submit_zip" --keychain-profile "$NOTARY_PROFILE" --wait
  info "stapling ticket"
  xcrun stapler staple "$APP"
  xcrun stapler validate "$APP"
fi

# ---------------------------------------------------------------------------
# Emit the final .zip
# ---------------------------------------------------------------------------
rm -f "$ZIP"
ditto -c -k --keepParent "$APP" "$ZIP"

info "done"
echo
echo "  app:  $APP"
echo "  zip:  $ZIP"
echo "  arch: $(lipo -archs "$MACOS_DIR/$APP_NAME" 2>/dev/null || echo '?')"
echo "  sign: $SIGN_FLOW"
