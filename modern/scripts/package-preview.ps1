[CmdletBinding()]
param(
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*$')]
    [string]$Version = '0.1.0-preview',
    [switch]$Cuda,
    [switch]$SkipNativeBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$modernRoot = Join-Path $repoRoot 'modern'
$preset = if ($Cuda) { 'windows-cuda' } else { 'windows-cpu' }
$buildPreset = if ($Cuda) { 'windows-cuda-release' } else { 'windows-cpu-release' }
$backendName = if ($Cuda) { 'cuda' } else { 'cpu' }
$artifactsRoot = Join-Path $modernRoot 'artifacts'
$packageRoot = Join-Path $artifactsRoot 'preview'
$publishRoot = Join-Path $artifactsRoot 'publish'
$legacyBaselineCommit = 'aae2b3e5310fcaad4151a878ad65ed2a3fa29146'
$legacyBaselineTag = 'legacy-baseline-aae2b3e'

$commit = (git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
    throw 'Unable to resolve the package source commit.'
}
$workingTreeState = @(git -C $repoRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect the Git working tree.' }
if ($workingTreeState.Count -ne 0) {
    throw 'Refusing to package an uncommitted working tree. Commit all source and release metadata first.'
}
$resolvedBaseline = (git -C $repoRoot rev-parse "$legacyBaselineTag^{commit}").Trim()
if ($LASTEXITCODE -ne 0 -or $resolvedBaseline -ne $legacyBaselineCommit) {
    throw "Preservation tag $legacyBaselineTag does not resolve to $legacyBaselineCommit."
}

function Reset-ChildDirectory([string]$Path, [string]$AllowedRoot) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullAllowedRoot = [IO.Path]::GetFullPath($AllowedRoot).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($fullAllowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a directory outside $fullAllowedRoot`: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
}

if (-not $SkipNativeBuild) {
    if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) {
        throw 'CMake was not found. Install Visual Studio 2022 Desktop development with C++ and CMake tools.'
    }
    Push-Location (Join-Path $modernRoot 'native')
    try {
        cmake --preset $preset
        if ($LASTEXITCODE -ne 0) { throw 'Native CMake configuration failed.' }
        cmake --build --preset $buildPreset
        if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
        ctest --preset $preset
        if ($LASTEXITCODE -ne 0) { throw 'Native tests failed.' }
    }
    finally {
        Pop-Location
    }
}

Reset-ChildDirectory -Path $publishRoot -AllowedRoot $artifactsRoot
Reset-ChildDirectory -Path $packageRoot -AllowedRoot $artifactsRoot

dotnet publish (Join-Path $modernRoot 'app\Dslt.App\Dslt.App.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishRoot `
    -p:Version=$Version `
    -p:ContinuousIntegrationBuild=true `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Managed publish failed.' }

$nativeDll = Join-Path $modernRoot "native\out\build\$preset\Release\dslt_core.dll"
if (-not (Test-Path -LiteralPath $nativeDll)) { throw "Native DLL was not found at $nativeDll" }
Copy-Item -LiteralPath $nativeDll -Destination $publishRoot -Force

$dotnetExecutable = (Get-Command dotnet -ErrorAction Stop).Source
$dotnetRoot = Split-Path -Parent $dotnetExecutable
$dotnetLicense = Join-Path $dotnetRoot 'LICENSE.txt'
$dotnetNotices = Join-Path $dotnetRoot 'ThirdPartyNotices.txt'
$docsRoot = Join-Path $publishRoot 'docs'
New-Item -ItemType Directory -Path $docsRoot -Force | Out-Null

$distributionFiles = [ordered]@{
    (Join-Path $repoRoot 'COPYING.GPLv3') = 'COPYING.GPLv3'
    (Join-Path $repoRoot 'license.txt') = 'LEGACY-THIRD-PARTY-NOTICES.txt'
    $dotnetLicense = 'DOTNET-LICENSE.txt'
    $dotnetNotices = 'DOTNET-THIRD-PARTY-NOTICES.txt'
    (Join-Path $modernRoot 'README.md') = 'README.md'
}
foreach ($entry in $distributionFiles.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $entry.Key)) { throw "Distribution file is missing: $($entry.Key)" }
    Copy-Item -LiteralPath $entry.Key -Destination (Join-Path $publishRoot $entry.Value) -Force
}
Get-ChildItem -LiteralPath (Join-Path $modernRoot 'docs') -Filter '*.md' -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $docsRoot $_.Name) -Force
}

$buildInfo = [ordered]@{
    schemaVersion = 1
    packageVersion = $Version
    runtimeIdentifier = 'win-x64'
    selfContained = $true
    backend = $backendName
    sourceCommit = $commit
    upstreamRepository = 'https://github.com/takashi310/DSLT'
    legacyBaselineCommit = $legacyBaselineCommit
    legacyBaselineTag = $legacyBaselineTag
    validationLevel = 'synthetic-data-validated'
}
$buildInfo | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publishRoot 'BUILD-INFO.json') -Encoding utf8

$archiveName = "dslt-workbench-$Version-win-x64-$backendName.zip"
$archive = Join-Path $packageRoot $archiveName
Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $archive -CompressionLevel Optimal -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = "$archive.sha256"
Set-Content -LiteralPath $checksumPath -Value "$hash  $archiveName" -Encoding ascii

& (Join-Path $PSScriptRoot 'verify-preview-package.ps1') -Archive $archive -Checksum $checksumPath
if ($LASTEXITCODE -ne 0) { throw 'Package verification failed.' }

Write-Host "Preview package: $archive"
Write-Host "SHA-256: $hash"
