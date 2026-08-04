[CmdletBinding()]
param(
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')]
    [string]$Version = '0.1.0-preview',
    [switch]$Cuda,
    [switch]$SkipNativeBuild,
    [string]$PrebuiltCudaArtifactDirectory,
    [string]$PrebuiltCudaSourceCommit,
    [switch]$ReleaseCandidate
)

$ErrorActionPreference = 'Stop'
if ($ReleaseCandidate) {
    if ($Version -ne '1.0.0' -or -not $Cuda) {
        throw 'The v1 release candidate must use -Version 1.0.0 and -Cuda.'
    }
}
elseif ($Version -notmatch '-') {
    throw 'A stable version requires the explicit -ReleaseCandidate switch.'
}
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$modernRoot = Join-Path $repoRoot 'modern'
if ($ReleaseCandidate) {
    $compatibilityMatrix = Get-Content -LiteralPath (Join-Path $modernRoot 'docs\compatibility-matrix.md') -Raw -Encoding utf8
    if ($compatibilityMatrix -match '\|\s*(scaffolded|pending-reference)\s*\|') {
        throw 'Release-candidate packaging is blocked while legacy capabilities remain unfinished.'
    }
}
$preset = if ($Cuda) { 'windows-cuda' } else { 'windows-cpu' }
$buildPreset = if ($Cuda) { 'windows-cuda-release' } else { 'windows-cpu-release' }
$backendName = if ($Cuda) { 'cuda' } else { 'cpu' }
$artifactsRoot = Join-Path $modernRoot 'artifacts'
$packageDirectoryName = if ($ReleaseCandidate) { 'release-candidate' } else { 'preview' }
$packageRoot = Join-Path $artifactsRoot $packageDirectoryName
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
$hasPrebuiltDirectory = -not [string]::IsNullOrWhiteSpace($PrebuiltCudaArtifactDirectory)
$hasPrebuiltCommit = -not [string]::IsNullOrWhiteSpace($PrebuiltCudaSourceCommit)
if ($hasPrebuiltDirectory -ne $hasPrebuiltCommit) {
    throw '-PrebuiltCudaArtifactDirectory and -PrebuiltCudaSourceCommit must be supplied together.'
}
if ($hasPrebuiltDirectory -and (-not $Cuda -or -not $SkipNativeBuild)) {
    throw 'A prebuilt CUDA artifact requires both -Cuda and -SkipNativeBuild.'
}
$resolvedPrebuiltDirectory = $null
$nativeSourceCommit = $commit
if ($hasPrebuiltDirectory) {
    if ($PrebuiltCudaSourceCommit -notmatch '^[0-9a-f]{40}$') {
        throw '-PrebuiltCudaSourceCommit must be a full lowercase Git commit.'
    }
    git -C $repoRoot cat-file -e "$PrebuiltCudaSourceCommit^{commit}"
    if ($LASTEXITCODE -ne 0) { throw 'The prebuilt CUDA source commit is not available in this repository.' }
    git -C $repoRoot merge-base --is-ancestor $PrebuiltCudaSourceCommit $commit
    if ($LASTEXITCODE -ne 0) { throw 'The prebuilt CUDA source commit must be an ancestor of the package commit.' }
    git -C $repoRoot diff --quiet "$PrebuiltCudaSourceCommit..$commit" -- modern/native
    if ($LASTEXITCODE -eq 1) {
        throw 'Native sources changed after the prebuilt CUDA artifact source commit.'
    }
    if ($LASTEXITCODE -ne 0) { throw 'Unable to compare prebuilt CUDA and package native sources.' }
    $resolvedPrebuiltDirectory = (Resolve-Path -LiteralPath $PrebuiltCudaArtifactDirectory).Path
    & (Join-Path $PSScriptRoot 'run-cuda-artifact-tests.ps1') -ArtifactDirectory $resolvedPrebuiltDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Prebuilt CUDA artifact runtime tests failed.' }
    $nativeSourceCommit = $PrebuiltCudaSourceCommit
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
    -p:SourceRevisionId=$commit `
    -p:ContinuousIntegrationBuild=true `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Managed publish failed.' }

$nativeDll = if ($null -ne $resolvedPrebuiltDirectory) {
    Join-Path $resolvedPrebuiltDirectory 'dslt_core.dll'
} else {
    Join-Path $modernRoot "native\out\build\$preset\Release\dslt_core.dll"
}
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
    (Join-Path $modernRoot 'legacy\DSLT_Demo_User_Manual_v1.11.pdf') = 'docs\DSLT_Demo_User_Manual_v1.11.pdf'
    (Join-Path $modernRoot 'legacy\README.md') = 'docs\legacy-assets.md'
}
foreach ($entry in $distributionFiles.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $entry.Key)) { throw "Distribution file is missing: $($entry.Key)" }
    Copy-Item -LiteralPath $entry.Key -Destination (Join-Path $publishRoot $entry.Value) -Force
}
Get-ChildItem -LiteralPath (Join-Path $modernRoot 'docs') -Filter '*.md' -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $docsRoot $_.Name) -Force
}
Get-ChildItem -LiteralPath (Join-Path $modernRoot 'validation') -Filter '*.example.json' -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $docsRoot $_.Name) -Force
}

$buildInfo = [ordered]@{
    schemaVersion = 1
    packageVersion = $Version
    runtimeIdentifier = 'win-x64'
    selfContained = $true
    backend = $backendName
    sourceCommit = $commit
    nativeSourceCommit = $nativeSourceCommit
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

$packageKind = if ($ReleaseCandidate) { 'Release candidate' } else { 'Preview' }
Write-Host "$packageKind package: $archive"
Write-Host "SHA-256: $hash"
