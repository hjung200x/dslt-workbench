[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactDirectory
)

$ErrorActionPreference = 'Stop'

$resolvedArtifact = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$testExecutables = @(Get-ChildItem -LiteralPath $resolvedArtifact -Recurse -File -Filter 'dslt_native_tests.exe')
if ($testExecutables.Count -ne 1) {
    throw "Expected exactly one dslt_native_tests.exe below '$resolvedArtifact'; found $($testExecutables.Count)."
}

$testExecutable = $testExecutables[0]
$bundleDirectory = $testExecutable.DirectoryName
$nativeLibrary = Join-Path $bundleDirectory 'dslt_core.dll'
if (-not (Test-Path -LiteralPath $nativeLibrary -PathType Leaf)) {
    throw "CUDA test bundle is missing '$nativeLibrary'."
}

$nvidiaSmi = Get-Command 'nvidia-smi.exe' -ErrorAction SilentlyContinue
if ($null -eq $nvidiaSmi) {
    throw 'nvidia-smi.exe was not found; a working NVIDIA driver and CUDA-capable GPU are required.'
}

$gpu = & $nvidiaSmi.Source '--query-gpu=name,driver_version,memory.total,compute_cap' '--format=csv,noheader'
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace(($gpu -join ''))) {
    throw 'Unable to query a CUDA-capable NVIDIA GPU.'
}
Write-Host "CUDA runtime test GPU: $($gpu -join '; ')"

$previousPath = $env:PATH
try {
    $env:PATH = "$bundleDirectory;$previousPath"
    Push-Location $bundleDirectory
    try {
        & $testExecutable.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "CUDA native tests failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    $env:PATH = $previousPath
}

Write-Host 'CUDA artifact runtime tests passed.'
