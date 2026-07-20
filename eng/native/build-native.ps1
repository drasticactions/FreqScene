# Usage: pwsh eng/native/build-native.ps1
$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path "$PSScriptRoot/../..").Path
$SrcDir = Join-Path $RepoRoot "external/projectm"
$BuildDir = Join-Path $RepoRoot "artifacts/native-build/win-x64"
$StageDir = Join-Path $RepoRoot "artifacts/native-install/win-x64"
$OutDir = Join-Path $RepoRoot "artifacts/native/win-x64"

git -C $RepoRoot submodule update --init --recursive external/projectm

cmake -S $SrcDir -B $BuildDir -A x64 `
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

Write-Host "native artifacts for win-x64 staged in ${OutDir}:"
Get-ChildItem $OutDir
