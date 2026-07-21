<#
.SYNOPSIS
  Build FreqScene into a portable, self-contained Windows zip (NativeAOT).

.DESCRIPTION
  Pipeline (per target architecture):
    1. Gate on the MSVC C++ toolchain NativeAOT needs (fail fast with the exact
       VS component to install; arm64 additionally needs the ARM64 VC tools).
    2. Ensure native libprojectM exists for the RID, auto-building any missing
       one via eng/native/build-native.ps1.
    3. Generate a multi-resolution FreqScene.ico from the PNG asset.
    4. `dotnet publish` NativeAOT (self-contained) into a "FreqScene" folder,
       embedding the icon via ApplicationIcon.
    5. Optionally Authenticode-sign the produced FreqScene.exe with signtool.
    6. Zip the folder to artifacts/pack/FreqScene-<version>-<arch>.zip.

  NativeAOT for win-arm64 cross-compiles on an x64 host, but the link step needs
  the ARM64 MSVC libraries (the same component build-native.ps1 needs for arm64).

.PARAMETER Arch
  Target architecture: x64, arm64, or both (default: both).

.PARAMETER Version
  Marketing version embedded in the zip name (default: 1.0.0).

.PARAMETER Output
  Output directory for the zip(s) (default: artifacts/pack).

.PARAMETER Icon
  Override the source icon image (default: eng/pack/assets/FreqScene.png).

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
  pwsh eng/pack/build-windows-zip.ps1
  pwsh eng/pack/build-windows-zip.ps1 -Arch x64 -Version 1.2.0
  pwsh eng/pack/build-windows-zip.ps1 -CertPath cert.pfx -CertPassword hunter2
#>
[CmdletBinding()]
param(
    [ValidateSet("x64", "arm64", "both")]
    [string]$Arch = "both",
    [string]$Version = "1.0.0",
    [string]$Output,
    [string]$Icon,
    [string]$CertPath,
    [string]$CertPassword,
    [string]$CertThumbprint,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

function Info($msg) { Write-Host "==> $msg" }
function Die($msg) { throw $msg }

if (-not $IsWindows) { Die "this script must run on Windows" }

$RepoRoot = (Resolve-Path "$PSScriptRoot/../..").Path
$Project = Join-Path $RepoRoot "src/FreqScene/FreqScene.csproj"
$AppName = "FreqScene"
if (-not $Output) { $Output = Join-Path $RepoRoot "artifacts/pack" }
if (-not $Icon) { $Icon = Join-Path $RepoRoot "eng/pack/assets/FreqScene.png" }
$NativeBuild = Join-Path $RepoRoot "eng/native/build-native.ps1"

[string[]]$Rids = switch ($Arch) {
    "x64"   { , "win-x64" }
    "arm64" { , "win-arm64" }
    "both"  { "win-x64", "win-arm64" }
}

$Signing = [bool]($CertPath -or $CertThumbprint)
if ($CertPath -and -not $CertPassword) { Die "-CertPassword is required with -CertPath" }
if (-not (Test-Path $Icon)) { Die "icon source not found: $Icon" }

# ---------------------------------------------------------------------------
# MSVC toolchain gate (NativeAOT links with MSVC + the Windows SDK).
# ---------------------------------------------------------------------------
# Enumerate every VS/BuildTools install (stable + prerelease/Insiders).
function Get-VsInstalls {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio/Installer/vswhere.exe"
    if (-not (Test-Path $vswhere)) { return @() }
    $paths = & $vswhere -all -prerelease -products * -property installationPath 2>$null
    return @($paths | Where-Object { $_ })
}

# Resolve the MSVC toolchain for a RID by the actual cross `cl.exe` on disk rather
# than by vswhere component IDs: newer installs (e.g. VS 2026 Insiders) ship the
# compiler and target libs but don't register the classic VC.Tools.* component IDs,
# so a `-requires` query gives a false negative even though AOT can link fine.
# Returns the vcvarsall.bat + a toolset version that has the target's cross tools,
# used both to gate up front and to set up the dev environment for `dotnet publish`
# (the .NET ILCompiler's own linker discovery fails to find the ARM64 linker on
# Insiders, so we publish inside a vcvarsall x64_arm64 environment instead).
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
                Cl             = Join-Path $ts.FullName "bin\Hostx64\$target\cl.exe"
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
  winget install --id Microsoft.VisualStudio.2022.BuildTools --override `
    "--add $component --add Microsoft.VisualStudio.Component.Windows11SDK.22621 --quiet"
"@
}

# ---------------------------------------------------------------------------
# signtool discovery (only when signing is requested).
# ---------------------------------------------------------------------------
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
# Icon: generate a multi-resolution .ico from the PNG (System.Drawing, else
# ImageMagick, else fail fast).
# ---------------------------------------------------------------------------
function New-Icon([string]$pngPath, [string]$icoPath) {
    $sizes = 16, 24, 32, 48, 64, 128, 256

    $drawing = $false
    foreach ($asm in "System.Drawing.Common", "System.Drawing") {
        try { Add-Type -AssemblyName $asm -ErrorAction Stop; $drawing = $true; break } catch {}
    }

    if ($drawing) {
        $src = [System.Drawing.Image]::FromFile((Resolve-Path $pngPath))
        try {
            $frames = foreach ($s in $sizes) {
                $bmp = New-Object System.Drawing.Bitmap $s, $s
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $g.DrawImage($src, 0, 0, $s, $s)
                $g.Dispose()
                $ms = New-Object System.IO.MemoryStream
                $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
                $bmp.Dispose()
                [pscustomobject]@{ Size = $s; Bytes = $ms.ToArray() }
            }
        } finally { $src.Dispose() }

        $fs = [System.IO.File]::Create($icoPath)
        $bw = New-Object System.IO.BinaryWriter $fs
        try {
            $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$frames.Count)
            $offset = 6 + 16 * $frames.Count
            foreach ($f in $frames) {
                $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
                $bw.Write([byte]$dim); $bw.Write([byte]$dim)   # width, height (0 == 256)
                $bw.Write([byte]0); $bw.Write([byte]0)          # colors, reserved
                $bw.Write([uint16]1); $bw.Write([uint16]32)     # planes, bit depth
                $bw.Write([uint32]$f.Bytes.Length)              # bytes in resource
                $bw.Write([uint32]$offset)                      # image offset
                $offset += $f.Bytes.Length
            }
            foreach ($f in $frames) { $bw.Write($f.Bytes) }
        } finally { $bw.Dispose(); $fs.Dispose() }
        return
    }

    if (Get-Command magick.exe -ErrorAction SilentlyContinue) {
        $list = ($sizes | Sort-Object -Descending) -join ","
        & magick.exe $pngPath -background none -define "icon:auto-resize=$list" $icoPath
        if ($LASTEXITCODE -ne 0) { Die "magick failed to generate $icoPath" }
        return
    }

    Die @"
Cannot generate an .ico: neither System.Drawing nor ImageMagick is available.
Install ImageMagick (winget install ImageMagick.ImageMagick) or run this script
under Windows PowerShell 5.1 where System.Drawing is present.
"@
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

$WorkDir = Join-Path $RepoRoot "artifacts/pack-work-windows"
if (Test-Path $WorkDir) { Remove-Item -Recurse -Force $WorkDir }
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

$IcoPath = Join-Path $WorkDir "$AppName.ico"
Info "generating icon $IcoPath from $Icon"
New-Icon $Icon $IcoPath

New-Item -ItemType Directory -Force -Path $Output | Out-Null
$results = @()

foreach ($rid in $Rids) {
    $imgArch = if ($rid -eq "win-arm64") { "arm64" } else { "x64" }

    # 2. Native libprojectM (auto-build if missing).
    $nativeDir = Join-Path $RepoRoot "artifacts/native/$rid"
    if (-not (Test-Path (Join-Path $nativeDir "projectM-4.dll"))) {
        Info "native libprojectM for $rid missing - building (build-native.ps1 -Rid $rid)"
        & $NativeBuild -Rid $rid
        if ($LASTEXITCODE -ne 0) { Die "native build failed for $rid" }
    } else {
        Info "native libprojectM for $rid present"
    }

    # 4. Publish NativeAOT into a "FreqScene" folder (so the zip has a top folder).
    #    Run inside a vcvarsall dev environment for the target arch AND pass
    #    IlcUseEnvironmentalTools=true. Otherwise the ILCompiler ignores the ambient
    #    environment and runs its own findvcvarsall.bat probe, which fails to locate
    #    the ARM64 linker on installs that don't register the classic VC component IDs
    #    ("Platform linker not found"). With the flag it uses link.exe + LIB from the
    #    vcvars environment we set up here instead.
    $vc = $VcByRid[$rid]
    $publishDir = Join-Path $WorkDir "$rid/$AppName"
    Info "dotnet publish $rid (NativeAOT) in a $($vc.HostArg) dev environment (toolset $($vc.ToolsetVersion))"
    $publishCmd = @(
        "set `"PATH=%PATH%;$InstallerDir`""
        "`"$($vc.VcVars)`" $($vc.HostArg) -vcvars_ver=$($vc.ToolsetVersion)"
        "dotnet publish `"$Project`" -c Release -r $rid --self-contained true " +
            "-p:PublishAot=true -p:IlcUseEnvironmentalTools=true " +
            "-p:DebugType=none -p:DebugSymbols=false " +
            "-p:ApplicationIcon=`"$IcoPath`" -o `"$publishDir`""
    ) -join " && "
    cmd /c $publishCmd
    if ($LASTEXITCODE -ne 0) { Die "dotnet publish failed for $rid" }

    $exe = Join-Path $publishDir "$AppName.exe"
    if (-not (Test-Path $exe)) { Die "AOT exe '$AppName.exe' not found in publish output for $rid" }

    # Prune stray cross-RID native assets: the csproj copies every listed RID's
    # projectM DLL unconditionally (so a plain `dotnet run` finds one), which leaves
    # the other arch's runtimes/<rid>/native in this single-RID package. Drop all but
    # the target RID's - the loader only ever probes runtimes/<current-rid>/native.
    $runtimesDir = Join-Path $publishDir "runtimes"
    if (Test-Path $runtimesDir) {
        Get-ChildItem $runtimesDir -Directory |
            Where-Object { $_.Name -ne $rid } |
            Remove-Item -Recurse -Force
    }

    # 5. Sign the AOT exe.
    if ($Signing) {
        $args = @("sign", "/fd", "SHA256", "/tr", $TimestampUrl, "/td", "SHA256")
        if ($CertPath) { $args += @("/f", $CertPath, "/p", $CertPassword) }
        else { $args += @("/sha1", $CertThumbprint) }
        $args += $exe
        Info "signing $AppName.exe ($rid)"
        & $signtool @args
        if ($LASTEXITCODE -ne 0) { Die "signtool failed for $rid" }
    }

    # 6. Zip.
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
