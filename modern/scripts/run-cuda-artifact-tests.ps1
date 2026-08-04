[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactDirectory,

    [string]$EvidencePath,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$resolvedArtifact = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sourceCommit = (git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') {
    throw 'Unable to resolve the CUDA test source commit.'
}

$evidenceFullPath = $null
$evidenceChecksumPath = $null
if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
    $evidenceFullPath = [IO.Path]::GetFullPath($EvidencePath)
    $evidenceChecksumPath = "$evidenceFullPath.sha256"
    if (((Test-Path -LiteralPath $evidenceFullPath) -or
         (Test-Path -LiteralPath $evidenceChecksumPath)) -and -not $Force) {
        throw "Evidence or its checksum already exists; pass -Force to replace it: $evidenceFullPath"
    }
    $evidenceDirectory = Split-Path -Parent $evidenceFullPath
    if (-not (Test-Path -LiteralPath $evidenceDirectory)) {
        New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
    }
}

$operationHeader = Join-Path $repoRoot 'modern\native\include\dslt\c_api.h'
$operationMatches = Select-String -LiteralPath $operationHeader -Pattern 'DSLT_OP_[A-Z0-9_]+' -AllMatches
$operations = @($operationMatches.Matches.Value | Sort-Object -Unique)
$expectedOperations = @(
    'DSLT_OP_ADAPTIVE_THRESHOLD_2D',
    'DSLT_OP_ADAPTIVE_THRESHOLD_3D',
    'DSLT_OP_CONNECTED_COMPONENTS',
    'DSLT_OP_COPY',
    'DSLT_OP_DEPTH_MAP',
    'DSLT_OP_DILATE_CUBE',
    'DSLT_OP_DILATE_SPHERE',
    'DSLT_OP_DSLT_SEGMENTATION',
    'DSLT_OP_DSLT_THRESHOLD',
    'DSLT_OP_ERODE_CUBE',
    'DSLT_OP_ERODE_SPHERE',
    'DSLT_OP_EXTRACT_XY',
    'DSLT_OP_EXTRACT_YZ',
    'DSLT_OP_EXTRACT_ZX',
    'DSLT_OP_H_MINIMA',
    'DSLT_OP_HEIGHT_MAP',
    'DSLT_OP_HEIGHT_PROJECTION',
    'DSLT_OP_Z_GRADIENT',
    'DSLT_OP_RESAMPLE_Z_AREA',
    'DSLT_OP_RESAMPLE_Z_LANCZOS',
    'DSLT_OP_SMOOTH_GAUSSIAN',
    'DSLT_OP_SMOOTH_MEAN',
    'DSLT_OP_THRESHOLD_2D',
    'DSLT_OP_THRESHOLD_3D',
    'DSLT_OP_THRESHOLD_SWEEP',
    'DSLT_OP_WATERSHED',
    'DSLT_OP_WINDOW_LEVEL'
)
if (Compare-Object $expectedOperations $operations) {
    throw 'The public C ABI operation set changed; update the CUDA parity test and evidence contract together.'
}
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

$evidence = [ordered]@{
    schemaVersion = 1
    capturedAtUtc = [DateTime]::UtcNow.ToString('O')
    passed = $false
    error = $null
    sourceCommit = $sourceCommit
    gpu = @($gpu)
    testBundle = [ordered]@{
        nativeTestsSha256 = (Get-FileHash -LiteralPath $testExecutable.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        nativeLibrarySha256 = (Get-FileHash -LiteralPath $nativeLibrary -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    parity = [ordered]@{
        publicOperationCount = $operations.Count
        publicOperations = $operations
        floatAbsoluteTolerance = 1.0e-5
        floatRelativeTolerance = 1.0e-4
        discreteVoxelExact = $true
        topologyExact = $true
        repeatedOperationCount = 100
        deviceMemoryGrowthToleranceBytes = 1048576
    }
}

$previousPath = $env:PATH
$failure = $null
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
catch {
    $failure = $_
    $evidence.error = $_.Exception.Message
}
finally {
    $env:PATH = $previousPath
    if ($null -ne $evidenceFullPath) {
        $evidence.passed = $null -eq $failure
        $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $evidenceFullPath -Encoding utf8
        $evidenceHash = (Get-FileHash -LiteralPath $evidenceFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $evidenceName = Split-Path -Leaf $evidenceFullPath
        Set-Content -LiteralPath $evidenceChecksumPath -Value "$evidenceHash  $evidenceName" -Encoding ascii
    }
}

if ($null -ne $failure) { throw $failure }
Write-Host 'CUDA artifact runtime tests passed.'
if ($null -ne $evidenceFullPath) {
    Write-Host "CUDA parity evidence: $evidenceFullPath"
    Write-Host "Evidence SHA-256: $evidenceHash"
}
