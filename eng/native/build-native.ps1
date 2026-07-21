# Usage: pwsh eng/native/build-native.ps1 [-Rid win-x64|win-arm64]
#
# Builds libprojectM (core + playlist) from the external/projectm submodule with
# MSVC/CMake and stages the two DLLs into artifacts/native/<rid>/.
#
# win-arm64 cross-compiles from an x64 host via the MSVC "-A ARM64" generator,
# which requires the "MSVC v143 - VS 2022 C++ ARM64 build tools" component to be
# installed (VS Installer / vs_buildtools). Without it, cmake configure fails.
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Rid = "win-x64"
)

$ErrorActionPreference = "Stop"

# Map the .NET RID to the MSVC generator platform (-A).
$Platform = switch ($Rid) {
    "win-x64"   { "x64" }
    "win-arm64" { "ARM64" }
}

$RepoRoot = (Resolve-Path "$PSScriptRoot/../..").Path
$SrcDir = Join-Path $RepoRoot "external/projectm"
$BuildDir = Join-Path $RepoRoot "artifacts/native-build/$Rid"
$StageDir = Join-Path $RepoRoot "artifacts/native-install/$Rid"
$OutDir = Join-Path $RepoRoot "artifacts/native/$Rid"

git -C $RepoRoot submodule update --init --recursive external/projectm

cmake -S $SrcDir -B $BuildDir -A $Platform `
  -DBUILD_SHARED_LIBS=ON `
  -DENABLE_PLAYLIST=ON `
  -DENABLE_SDL_UI=OFF `
  -DENABLE_DEBUG_POSTFIX=OFF `
  -DENABLE_INSTALL=ON `
  -DBUILD_TESTING=OFF `
  "-DCMAKE_INSTALL_PREFIX=$StageDir"
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed" }

cmake --build $BuildDir --config Release --parallel
if ($LASTEXITCODE -ne 0) { throw "cmake build failed" }

cmake --install $BuildDir --config Release
if ($LASTEXITCODE -ne 0) { throw "cmake install failed" }

if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Copy-Item (Join-Path $StageDir "bin/projectM-4.dll") $OutDir
Copy-Item (Join-Path $StageDir "bin/projectM-4-playlist.dll") $OutDir

foreach ($dll in "projectM-4.dll", "projectM-4-playlist.dll") {
    if (-not (Test-Path (Join-Path $OutDir $dll))) { throw "missing $dll" }
}

Write-Host "native artifacts for $Rid staged in ${OutDir}:"
Get-ChildItem $OutDir
