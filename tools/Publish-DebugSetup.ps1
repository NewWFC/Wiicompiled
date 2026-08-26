# Publishes WiiCompiled.Setup as a self-contained Debug exe (no optimizations, full PDBs) to
# Launcher/dist/debug, renames it to match the public WiiCompiled-Setup.exe name (dotnet publish's
# natural output is WiiCompiled.Setup.exe, matching the assembly name), then re-hosts the real
# installer payload from Launcher/dist/WiiCompiled-Setup.exe onto it.
#
# Why re-host rather than just publish: PayloadArchive.OpenCurrent() reads a Toolkit/BuildWorkspace
# payload appended to the running exe's own file - a plain dotnet publish output never has one, so
# a bare Debug host can run read-only commands (--self-test, --check-products) but not
# --silent/--repair. Re-hosting copies the *same* payload bytes Build-Installer.ps1 already
# produced onto the Debug-published host, so it gets .NET Debug diagnostics and still installs.
#
# Requires Launcher/dist/WiiCompiled-Setup.exe (the real, payload-bearing Release build from
# Build-Installer.ps1) to already exist - this script never builds or downloads a payload itself.
[CmdletBinding()]
param(
    [string]$SourcePayloadExe = 'Launcher/dist/WiiCompiled-Setup.exe',
    [string]$OutputDirectory = 'Launcher/dist/debug'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourcePayloadExe = [IO.Path]::GetFullPath((Join-Path $repoRoot $SourcePayloadExe))
$outputDirectory = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
if (-not (Test-Path -LiteralPath $sourcePayloadExe -PathType Leaf)) {
    throw "No payload-bearing setup exe at $sourcePayloadExe - run the 'WiiCompiled: Build installer' task first."
}

Write-Host '== Publishing WiiCompiled.Setup (Debug) =='
& dotnet publish (Join-Path $repoRoot 'Launcher/WiiCompiled.Setup/WiiCompiled.Setup.csproj') `
    -c Debug -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $outputDirectory
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$publishedHost = Join-Path $outputDirectory 'WiiCompiled.Setup.exe'
$debugSetupExe = Join-Path $outputDirectory 'WiiCompiled-Setup.exe'
Move-Item -LiteralPath $publishedHost -Destination $debugSetupExe -Force

Write-Host '== Re-hosting the real installer payload onto the Debug host =='
# Footer format matches Build-Installer.ps1's own append step: 8-byte ASCII magic, then two
# little-endian Int64s (payload offset, payload length), at the very end of the file.
$footerLength = 24
$sourceBytes = [IO.File]::ReadAllBytes($sourcePayloadExe)
if ($sourceBytes.Length -le $footerLength) {
    throw "$sourcePayloadExe is too small to carry a payload footer; is it a bare host too?"
}
$footer = $sourceBytes[($sourceBytes.Length - $footerLength)..($sourceBytes.Length - 1)]
$magic = [Text.Encoding]::ASCII.GetString($footer[0..7])
if ($magic -ne 'MKWCPAY1') {
    throw "$sourcePayloadExe does not end in a recognized MKWCPAY1 payload footer (found '$magic')."
}
$payloadOffset = [BitConverter]::ToInt64($footer, 8)
$payloadLength = [BitConverter]::ToInt64($footer, 16)
$payloadBytes = New-Object byte[] $payloadLength
[Array]::Copy($sourceBytes, $payloadOffset, $payloadBytes, 0, $payloadLength)

$debugHostBytes = [IO.File]::ReadAllBytes($debugSetupExe)
$outStream = [IO.File]::Open($debugSetupExe, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $outStream.Write($debugHostBytes, 0, $debugHostBytes.Length)
    $newOffset = $outStream.Position
    $outStream.Write($payloadBytes, 0, $payloadBytes.Length)
    $writer = [IO.BinaryWriter]::new($outStream, [Text.Encoding]::ASCII, $true)
    try {
        $writer.Write([Text.Encoding]::ASCII.GetBytes('MKWCPAY1'))
        $writer.Write([Int64]$newOffset)
        $writer.Write([Int64]$payloadBytes.Length)
    } finally { $writer.Dispose() }
} finally { $outStream.Dispose() }

$result = Get-Item -LiteralPath $debugSetupExe
Write-Host ("Done: {0} ({1:N1} MiB, payload re-hosted from {2})" -f `
    $result.FullName, ($result.Length / 1MB), $SourcePayloadExe)
