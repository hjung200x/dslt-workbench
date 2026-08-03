[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Cuda
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$modernRoot = Join-Path $repoRoot 'modern'
$preset = if ($Cuda) { 'windows-cuda' } else { 'windows-cpu' }
$buildPreset = if ($Cuda) { 'windows-cuda-release' } else { 'windows-cpu-release' }
$packageRoot = Join-Path $modernRoot 'artifacts\preview'
$publishRoot = Join-Path $modernRoot 'artifacts\publish'

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

dotnet publish (Join-Path $modernRoot 'app\Dslt.App\Dslt.App.csproj') `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'Managed publish failed.' }

$nativeDll = Join-Path $modernRoot "native\out\build\$preset\$Configuration\dslt_core.dll"
if (-not (Test-Path $nativeDll)) { throw "Native DLL was not found at $nativeDll" }
Copy-Item -LiteralPath $nativeDll -Destination $publishRoot -Force

New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'license.txt') -Destination $publishRoot -Force
Copy-Item -LiteralPath (Join-Path $modernRoot 'README.md') -Destination $publishRoot -Force
Copy-Item -LiteralPath (Join-Path $modernRoot 'docs\provenance.md') -Destination $publishRoot -Force
Copy-Item -LiteralPath (Join-Path $modernRoot 'docs\compatibility-matrix.md') -Destination $publishRoot -Force
Copy-Item -LiteralPath (Join-Path $modernRoot 'docs\functional-spec.md') -Destination $publishRoot -Force
Copy-Item -LiteralPath (Join-Path $modernRoot 'docs\validation-policy.md') -Destination $publishRoot -Force

$archive = Join-Path $packageRoot "dslt-workbench-preview-$Configuration-win-x64.zip"
Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$archive.sha256" -Value "$hash  $(Split-Path $archive -Leaf)" -Encoding ascii
Write-Host "Preview package: $archive"
Write-Host "SHA-256: $hash"
