# because I couldn't get dependencies before xd

# Downloads and lays out the pinned offline dependency sources Build-Installer.ps1 requires under
# Launcher/artifacts/dependencies. Each entry mirrors a FetchContent_Declare(...) in
# aurora-main/extern/CMakeLists.txt (or the Dawn/SDL3 provider modules); the URL and SHA-256 here
# must match those exactly, since NativeBuildFlags.ps1 points CMake at these directories via
# FETCHCONTENT_SOURCE_DIR_<NAME> instead of letting CMake download them (the build runs with
# -DFETCHCONTENT_FULLY_DISCONNECTED=ON).
#
# native_prebuilt is deliberately NOT fetched here: Build-Installer.ps1 builds it itself, from
# these sources, via Prepare-NativePrebuilt.ps1.
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$DependencySourceDirectory = 'Launcher/artifacts/dependencies',
    [string]$CppWinRtVersion = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dependencies = [IO.Path]::GetFullPath((Join-Path $repoRoot $DependencySourceDirectory))
[IO.Directory]::CreateDirectory($dependencies) | Out-Null

$tar = Join-Path $env:SystemRoot 'System32\tar.exe'
if (-not (Test-Path -LiteralPath $tar -PathType Leaf)) { throw "Windows archive tool is missing: $tar" }

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Install-PinnedSource {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string]$Url,
        [string]$Sha256
    )
    $destination = Join-Path $dependencies $Name
    if (Test-Path -LiteralPath $destination) {
        Write-Host "[skip] $Name already present at $destination"
        return
    }
    Write-Host "[fetch] $Name <- $Url"
    $work = Join-Path ([IO.Path]::GetTempPath()) ('mkwdep-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($work) | Out-Null
    try {
        $archivePath = Join-Path $work ([IO.Path]::GetFileName(([Uri]$Url).AbsolutePath))
        Invoke-WebRequest -Uri $Url -OutFile $archivePath -UseBasicParsing
        if ($Sha256) {
            $actual = Get-Sha256 $archivePath
            if ($actual -ne $Sha256.ToLowerInvariant()) {
                throw "$Name`: SHA-256 mismatch (expected $Sha256, got $actual) - refusing a tampered/wrong download."
            }
        } else {
            Write-Host "  (no pinned hash recorded upstream for $Name; not verified)"
        }
        $extractDir = Join-Path $work 'extract'
        [IO.Directory]::CreateDirectory($extractDir) | Out-Null
        & $tar -xf $archivePath -C $extractDir
        if ($LASTEXITCODE -ne 0) { throw "$Name`: extraction failed with exit code $LASTEXITCODE." }

        # Mirror FetchContent's single-top-level-directory flattening (GitHub tarballs wrap their
        # content in one directory; FETCHCONTENT_SOURCE_DIR_* overrides skip the step that would
        # normally flatten that for us, so the aurora CMakeLists' SOURCE_SUBDIR / bare CMakeLists.txt
        # expectations line up only if we do it here).
        $entries = @(Get-ChildItem -LiteralPath $extractDir)
        $sourceRoot = $extractDir
        if ($entries.Count -eq 1 -and $entries[0].PSIsContainer) { $sourceRoot = $entries[0].FullName }
        Move-Item -LiteralPath $sourceRoot -Destination $destination
    } finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Host "[done] $Name -> $destination"
}

# --- aurora-main/extern/CMakeLists.txt ---
Install-PinnedSource -Name 'abseil-cpp' `
    -Url 'https://github.com/abseil/abseil-cpp/archive/refs/tags/20240722.0.tar.gz'
Install-PinnedSource -Name 'xxhash' `
    -Url 'https://github.com/Cyan4973/xxHash/archive/refs/tags/v0.8.3.tar.gz' `
    -Sha256 'aae608dfe8213dfd05d909a57718ef82f30722c392344583d3f39050c7f29a80'
Install-PinnedSource -Name 'fmt' `
    -Url 'https://github.com/fmtlib/fmt/archive/refs/tags/11.1.4.tar.gz' `
    -Sha256 'ac366b7b4c2e9f0dde63a59b3feb5ee59b67974b14ee5dc9ea8ad78aa2c1ee1e'
Install-PinnedSource -Name 'zlib' `
    -Url 'https://github.com/madler/zlib/releases/download/v1.3.2/zlib-1.3.2.tar.gz' `
    -Sha256 'bb329a0a2cd0274d05519d61c667c062e06990d72e125ee2dfa8de64f0119d16'
Install-PinnedSource -Name 'png' `
    -Url 'https://github.com/pnggroup/libpng/archive/refs/tags/v1.6.58.tar.gz' `
    -Sha256 'a9d4df463d36a6e5f9c29bd6f4967312d17e996c1854f3511f833924eb1993cf'
Install-PinnedSource -Name 'freetype' `
    -Url 'https://files.twilitrealm.dev/freetype-2.14.3.tar.gz' `
    -Sha256 'e61b31ab26358b946e767ed7eb7f4bb2e507da1cfefeb7a8861ace7fd5c899a1'
Install-PinnedSource -Name 'imgui' `
    -Url 'https://github.com/ocornut/imgui/archive/refs/tags/v1.91.9b-docking.tar.gz' `
    -Sha256 '466fdef9b18de15f0bb6e288e3d00ffa3d82200ec458ce5e4f724a161d9528a5'
Install-PinnedSource -Name 'sqlite3' `
    -Url 'https://sqlite.org/2026/sqlite-amalgamation-3510300.zip' `
    -Sha256 'acb1e6f5d832484bf6d32b681e858c38add8b2acdfd42ac5df24b8afb46552b4'
Install-PinnedSource -Name 'zstd' `
    -Url 'https://github.com/facebook/zstd/releases/download/v1.5.7/zstd-1.5.7.tar.gz' `
    -Sha256 'eb33e51f49a15e023950cd7825ca74a4a2b43db8354825ac24fc1b7ee09e6fa3'
Install-PinnedSource -Name 'tracy' `
    -Url 'https://github.com/wolfpld/tracy/archive/a64b9a20294d59421a2f57aeca3c6383d8c48169.tar.gz' `
    -Sha256 '24d342b5127d7f659dc3cf94f24347b348cc736f25b17a691fd7f69541937658'

# --- aurora-main/cmake/AuroraDawnProvider.cmake (AURORA_DAWN_PROVIDER=package) ---
# aurora-main/CMakeLists.txt pins AURORA_DAWN_VERSION=v20260603.191052; the hash below is the one
# AuroraDawnProvider.cmake itself pins for that version's windows-amd64 asset.
Install-PinnedSource -Name 'dawn_prebuilt' `
    -Url 'https://github.com/encounter/dawn-build/releases/download/v20260603.191052/dawn-windows-amd64.tar.gz' `
    -Sha256 '7785373d569b3b0237918ec9c523239f7d0667857c5ea8242e3cdfde95e6aeab'

# --- aurora-main/cmake/AuroraSDL3Provider.cmake (AURORA_SDL3_PROVIDER=vendor) ---
# aurora-main/CMakeLists.txt pins AURORA_SDL3_VERSION=3.4.4; upstream does not publish a pinned
# hash for this asset.
Install-PinnedSource -Name 'SDL' `
    -Url 'https://github.com/libsdl-org/SDL/releases/download/release-3.4.4/SDL3-3.4.4.tar.gz'

# --- C++/WinRT headers (not a FetchContent dependency; NativeBuildFlags.ps1 wires it in only if
# cppwinrt/winrt/base.h exists) ---
$cppWinRtDestination = Join-Path $dependencies 'cppwinrt'
if (Test-Path -LiteralPath $cppWinRtDestination) {
    Write-Host "[skip] cppwinrt already present at $cppWinRtDestination"
} else {
    if ([string]::IsNullOrWhiteSpace($CppWinRtVersion)) {
        Write-Host '[resolve] Looking up the latest Microsoft.Windows.CppWinRT release on NuGet...'
        $versions = Invoke-RestMethod -Uri 'https://api.nuget.org/v3-flatcontainer/microsoft.windows.cppwinrt/index.json' -UseBasicParsing
        $CppWinRtVersion = @($versions.versions)[-1]
    }
    Write-Host "[fetch] cppwinrt <- Microsoft.Windows.CppWinRT $CppWinRtVersion (NuGet)"
    $work = Join-Path ([IO.Path]::GetTempPath()) ('mkwdep-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($work) | Out-Null
    try {
        $nupkgPath = Join-Path $work 'cppwinrt.zip'
        Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/Microsoft.Windows.CppWinRT/$CppWinRtVersion" `
            -OutFile $nupkgPath -UseBasicParsing
        $extractDir = Join-Path $work 'extract'
        [IO.Directory]::CreateDirectory($extractDir) | Out-Null
        & $tar -xf $nupkgPath -C $extractDir
        if ($LASTEXITCODE -ne 0) { throw "cppwinrt: extraction failed with exit code $LASTEXITCODE." }
        # Modern Microsoft.Windows.CppWinRT packages ship only the cppwinrt.exe generator tool, not
        # pre-generated headers. `-input local` projects from %WinDir%\System32\WinMetadata, which
        # every Windows 10/11 install carries (no Windows SDK required); `-include` scopes the
        # projection to the namespaces runtime/src/music_attenuation.cpp actually uses (SMTC-based
        # music ducking: Windows.Foundation[.Collections] + Windows.Media[.Control]), matching this
        # project's habit of explicit allowlists over "just grab everything". `-base` still forces
        # winrt/base.h to be emitted even if nothing else happened to need it.
        $cppWinRtExe = Join-Path $extractDir 'bin\cppwinrt.exe'
        if (-not (Test-Path -LiteralPath $cppWinRtExe -PathType Leaf)) {
            throw "cppwinrt: bin/cppwinrt.exe was not found in the NuGet package; pass -CppWinRtVersion for a specific release."
        }
        $includeDir = Join-Path $work 'generated'
        [IO.Directory]::CreateDirectory($includeDir) | Out-Null
        & $cppWinRtExe '-input' 'local' '-include' 'Windows.Foundation' '-include' 'Windows.Media' `
            '-base' '-output' $includeDir
        if ($LASTEXITCODE -ne 0) { throw "cppwinrt: header generation failed with exit code $LASTEXITCODE." }
        foreach ($required in @('winrt\base.h', 'winrt\Windows.Foundation.h',
                'winrt\Windows.Foundation.Collections.h', 'winrt\Windows.Media.Control.h')) {
            if (-not (Test-Path -LiteralPath (Join-Path $includeDir $required) -PathType Leaf)) {
                throw "cppwinrt: expected header was not produced: $required"
            }
        }
        # Build-Installer.ps1 also copies cppwinrt/LICENSE.txt into the payload's license
        # inventory; the package root ships it as plain "LICENSE".
        $licenseSource = Join-Path $extractDir 'LICENSE'
        if (Test-Path -LiteralPath $licenseSource -PathType Leaf) {
            Copy-Item -LiteralPath $licenseSource -Destination (Join-Path $includeDir 'LICENSE.txt')
        }
        Move-Item -LiteralPath $includeDir -Destination $cppWinRtDestination
    } finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Host "[done] cppwinrt -> $cppWinRtDestination"
}

Write-Host ''
Write-Host "Pinned offline dependency sources are ready under $dependencies."
Write-Host 'native_prebuilt was intentionally skipped - Build-Installer.ps1 builds it itself from these sources.'
