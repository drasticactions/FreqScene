<#
.SYNOPSIS
  Build FreqScene.Tui into a portable, self-contained Windows zip (NativeAOT).

.DESCRIPTION
  Pipeline (per target architecture):
    1. Gate on the MSVC C++ toolchain NativeAOT needs (fail fast with the exact
       VS component to install; arm64 additionally needs the ARM64 VC tools).
    2. `dotnet publish` NativeAOT (self-contained) into a "FreqScene.Tui" folder.
    3. Optionally Authenticode-sign the produced FreqScene.Tui.exe with signtool.
    4. Zip the folder to artifacts/pack/FreqScene.Tui-<version>-<arch>.zip.

  Unlike the desktop head this ships no native libprojectM: the TUI never creates
  a ProjectM instance (it starts with rendering stopped and attaches no
  IVisualizerHost), so the DllImport resolver in NativeLoader is never hit. The
  only native asset in the package is the OpenAL-Soft runtime that
  Silk.NET.OpenAL.Soft.Native brings in for CaptureAudioSource.

  NativeAOT for win-arm64 cross-compiles on an x64 host, but the link step needs
  the ARM64 MSVC libraries.

.PARAMETER Arch
  Target architecture: x64, arm64, or both (default: x64).

.PARAMETER Version
  Version embedded in the zip name (default: 1.0.0).

.PARAMETER Output
  Output directory for the zip(s) (default: artifacts/pack).

.PARAMETER CertPath
  Path to a PFX for Authenticode signing. Requires -CertPassword.

.PARAMETER CertPassword
  Password for the PFX given by -CertPath.

.PARAMETER CertThumbprint
  SHA1 thumbprint of a cert in the Windows certificate store to sign with
  (alternative to -CertPath/-CertPassword).

.PARAMETER TimestampUrl
  RFC 3161 timestamp server used when signing (default: DigiCert).

.EXAMPLE
  pwsh eng/pack/build-tui.ps1
  pwsh eng/pack/build-tui.ps1 -Arch both -Version 1.2.0
#>
[CmdletBinding()]
param(
    [ValidateSet("x64", "arm64", "both")]
    [string]$Arch = "x64",
    [string]$Version = "1.0.0",
    [string]$Output,
    [string]$CertPath,
    [string]$CertPassword,
    [string]$CertThumbprint,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

function Info($msg) { Write-Host "==> $msg" }
function Die($msg) { throw $msg }

if (-not $IsWindows) { Die "this script must run on Windows (macOS/Linux use build-tui.sh)" }

$RepoRoot = (Resolve-Path "$PSScriptRoot/../..").Path
$Project = Join-Path $RepoRoot "src/FreqScene.Tui/FreqScene.Tui.csproj"
$AppName = "FreqScene.Tui"
if (-not $Output) { $Output = Join-Path $RepoRoot "artifacts/pack" }

[string[]]$Rids = switch ($Arch) {
    "x64"   { , "win-x64" }
    "arm64" { , "win-arm64" }
    "both"  { "win-x64", "win-arm64" }
}

$Signing = [bool]($CertPath -or $CertThumbprint)
if ($CertPath -and -not $CertPassword) { Die "-CertPassword is required with -CertPath" }

# ---------------------------------------------------------------------------
# MSVC toolchain gate (NativeAOT links with MSVC + the Windows SDK).
# Mirrors the resolution in build-windows-zip.ps1 — see the comments there for
# why this probes for cl.exe on disk rather than querying vswhere components.
# ---------------------------------------------------------------------------
function Get-VsInstalls {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio/Installer/vswhere.exe"
    if (-not (Test-Path $vswhere)) { return @() }
    $paths = & $vswhere -all -prerelease -products * -property installationPath 2>$null
    return @($paths | Where-Object { $_ })
}

function Resolve-VcTools([string]$rid) {
    $target = if ($rid -eq "win-arm64") { "arm64" } else { "x64" }

    foreach ($install in (Get-VsInstalls)) {
        $vcvars = Join-Path $install "VC\Auxiliary\Build\vcvarsall.bat"
        $msvc = Join-Path $install "VC\Tools\MSVC"
        if (-not (Test-Path $vcvars) -or -not (Test-Path $msvc)) { continue }
        $ts = Get-ChildItem $msvc -Directory |
            Where-Object { Test-Path (Join-Path $_.FullName "bin\Hostx64\$target\cl.exe") } |
            Sort-Object Name -Descending | Select-Object -First 1
        if ($ts) {
            return [pscustomobject]@{
                Rid            = $rid
                VcVars         = $vcvars
                ToolsetVersion = $ts.Name
                HostArg        = if ($target -eq "arm64") { "x64_arm64" } else { "x64" }
            }
        }
    }

    $component = if ($rid -eq "win-arm64") { "Microsoft.VisualStudio.Component.VC.Tools.ARM64" }
                 else { "Microsoft.VisualStudio.Component.VC.Tools.x86.x64" }
    $label = if ($rid -eq "win-arm64") { "MSVC C++ ARM64 build tools" }
             else { "MSVC C++ x64/x86 build tools" }
    Die @"
NativeAOT for $rid requires the MSVC C++ toolchain (a Hostx64\$target\cl.exe under
VC\Tools\MSVC), which was not found in any Visual Studio install.
Install the '$label' component (and the Windows 10/11 SDK) via the Visual Studio
Installer, or run:
  winget install --id Microsoft.VisualStudio.2022.BuildTools --override ``
    "--add $component --add Microsoft.VisualStudio.Component.Windows11SDK.22621 --quiet"
"@
}

function Resolve-SignTool {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits/10/bin"
    if (Test-Path $kits) {
        $tool = Get-ChildItem -Path $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "\\x64\\" } |
            Sort-Object FullName -Descending | Select-Object -First 1
        if ($tool) { return $tool.FullName }
    }
    Die "signtool.exe not found (needed for signing). Install the Windows SDK or put signtool on PATH."
}

# ---------------------------------------------------------------------------
# Pipeline
# ---------------------------------------------------------------------------
Info "target rids: $($Rids -join ', ')"
$VcByRid = @{}
foreach ($rid in $Rids) {
    $vc = Resolve-VcTools $rid
    Info "MSVC toolchain for ${rid}: toolset $($vc.ToolsetVersion) ($($vc.HostArg)) under $(Split-Path $vc.VcVars)"
    $VcByRid[$rid] = $vc
}

# Directory holding vswhere; put it on PATH so vcvarsall can locate the VS install
# and Windows SDK (otherwise it warns 'vswhere not recognized' and may under-configure).
$InstallerDir = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"

$signtool = if ($Signing) { Resolve-SignTool } else { $null }
if ($Signing) { Info "signing with: $signtool" }

$WorkDir = Join-Path $RepoRoot "artifacts/pack-work-tui-windows"
if (Test-Path $WorkDir) { Remove-Item -Recurse -Force $WorkDir }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
New-Item -ItemType Directory -Force -Path $Output | Out-Null

$results = @()

foreach ($rid in $Rids) {
    $imgArch = if ($rid -eq "win-arm64") { "arm64" } else { "x64" }

    # Publish inside a vcvarsall dev environment for the target arch AND pass
    # IlcUseEnvironmentalTools=true, so the ILCompiler uses link.exe + LIB from
    # that environment instead of its own findvcvarsall.bat probe (which fails to
    # locate the ARM64 linker on installs that don't register the classic VC
    # component IDs). Same rationale as build-windows-zip.ps1.
    $vc = $VcByRid[$rid]
    $publishDir = Join-Path $WorkDir "$rid/$AppName"
    Info "dotnet publish $rid (NativeAOT) in a $($vc.HostArg) dev environment (toolset $($vc.ToolsetVersion))"
    $publishCmd = @(
        "set `"PATH=%PATH%;$InstallerDir`""
        "`"$($vc.VcVars)`" $($vc.HostArg) -vcvars_ver=$($vc.ToolsetVersion)"
        "dotnet publish `"$Project`" -c Release -r $rid --self-contained true " +
            "-p:PublishAot=true -p:IlcUseEnvironmentalTools=true " +
            "-p:DebugType=none -p:DebugSymbols=false " +
            "-o `"$publishDir`""
    ) -join " && "
    cmd /c $publishCmd
    if ($LASTEXITCODE -ne 0) { Die "dotnet publish failed for $rid" }

    $exe = Join-Path $publishDir "$AppName.exe"
    if (-not (Test-Path $exe)) { Die "AOT exe '$AppName.exe' not found in publish output for $rid" }

    if ($Signing) {
        $args = @("sign", "/fd", "SHA256", "/tr", $TimestampUrl, "/td", "SHA256")
        if ($CertPath) { $args += @("/f", $CertPath, "/p", $CertPassword) }
        else { $args += @("/sha1", $CertThumbprint) }
        $args += $exe
        Info "signing $AppName.exe ($rid)"
        & $signtool @args
        if ($LASTEXITCODE -ne 0) { Die "signtool failed for $rid" }
    }

    $zip = Join-Path $Output "$AppName-$Version-$imgArch.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Info "zipping $zip"
    Compress-Archive -Path $publishDir -DestinationPath $zip -CompressionLevel Optimal
    $results += [pscustomobject]@{ Rid = $rid; Zip = $zip }
}

Write-Host ""
Info "done"
foreach ($r in $results) {
    Write-Host "  $($r.Rid): $($r.Zip)"
}
Write-Host "  version: $Version"
Write-Host "  signed:  $Signing"
