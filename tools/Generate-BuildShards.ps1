# Runs the translator's three "developer build" commands against the repo root so
# generated/build_shards/shards.cmake exists, which Prepare-NativePrebuilt.ps1 (and therefore
# Build-Installer.ps1) requires before it can configure runtime/. See translator/README.md for
# the four-command translation flow this mirrors (dotnet build is the first of the four; this
# script covers the remaining three).
#
# Requires Assets/main.dol and Assets/StaticR.rel to already be in place (your own clean PAL
# RMCP01 dump; see the README - nobody here provides those files).
[CmdletBinding()]
param(
    [string]$Project = 'projects/mkwii/recomp.yml',
    [string]$EntryPoint = '0x800060A4'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$translator = Join-Path $repoRoot 'translator\src\Translator.Cli\bin\Release\net8.0\Translator.Cli.dll'
if (-not (Test-Path -LiteralPath $translator -PathType Leaf)) {
    throw "Translator CLI is not built: $translator (run the 'Build translator CLI' task, or 'dotnet build translator/src/Translator.Cli/Translator.Cli.csproj -c Release')"
}

function Invoke-Translator([string[]]$TranslatorArguments, [string]$Description) {
    Write-Host "== $Description =="
    & dotnet $translator @TranslatorArguments
    if ($LASTEXITCODE -ne 0) { throw "$Description failed with exit code $LASTEXITCODE." }
}

$functionsDir = 'generated/functions'
$baseMetadata = 'generated/base_translation_output.json'

Push-Location $repoRoot
try {
    Invoke-Translator @('translate-recursive', $EntryPoint, '--project', $Project,
            '--outdir', $functionsDir, '--output-metadata', $baseMetadata, '--prefer-cached-inputs') `
        'Translating the base game (translate-recursive)'
    Invoke-Translator @('generate-data-init', '--project', $Project) `
        'Generating data initialization (generate-data-init)'
    Invoke-Translator @('emit-build-shards', '--project', $Project,
            '--base-metadata', $baseMetadata, '--base-functions-dir', $functionsDir,
            '--native-source-dir', 'runtime/src') `
        'Emitting native build shards (emit-build-shards)'
} finally { Pop-Location }

Write-Host ''
Write-Host 'generated/build_shards/shards.cmake is ready.'
