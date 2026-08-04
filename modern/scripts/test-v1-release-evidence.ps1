[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ("dslt-v1-gate-test-" + [Guid]::NewGuid().ToString('N'))
$sourceCommit = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
$legacyCommit = 'aae2b3e5310fcaad4151a878ad65ed2a3fa29146'
$operations = @(
    'DSLT_OP_ADAPTIVE_THRESHOLD_2D', 'DSLT_OP_ADAPTIVE_THRESHOLD_3D',
    'DSLT_OP_CONNECTED_COMPONENTS', 'DSLT_OP_COPY', 'DSLT_OP_DEPTH_MAP',
    'DSLT_OP_DILATE_CUBE', 'DSLT_OP_DILATE_SPHERE', 'DSLT_OP_DSLT_SEGMENTATION',
    'DSLT_OP_DSLT_THRESHOLD', 'DSLT_OP_ERODE_CUBE', 'DSLT_OP_ERODE_SPHERE',
    'DSLT_OP_EXTRACT_XY', 'DSLT_OP_EXTRACT_YZ', 'DSLT_OP_EXTRACT_ZX',
    'DSLT_OP_H_MINIMA', 'DSLT_OP_HEIGHT_MAP', 'DSLT_OP_HEIGHT_PROJECTION',
    'DSLT_OP_RESAMPLE_Z_AREA', 'DSLT_OP_RESAMPLE_Z_LANCZOS',
    'DSLT_OP_SMOOTH_GAUSSIAN', 'DSLT_OP_SMOOTH_MEAN', 'DSLT_OP_THRESHOLD_2D',
    'DSLT_OP_THRESHOLD_3D', 'DSLT_OP_THRESHOLD_SWEEP', 'DSLT_OP_WATERSHED',
    'DSLT_OP_WINDOW_LEVEL'
)

function Write-Json([string]$Path, [object]$Value) {
    $Value | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Read-JsonFile([string]$Path, [string]$Description) {
    try {
        return Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json
    }
    catch {
        throw "$Description is not valid JSON: $($_.Exception.Message)"
    }
}

function Write-Checksum([string]$Path) {
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    $name = Split-Path -Leaf $Path
    Set-Content -LiteralPath "$Path.sha256" -Value "$hash  $name" -Encoding ascii
    return $hash
}

function Write-JsonWithChecksum([string]$Path, [object]$Value) {
    Write-Json $Path $Value
    return Write-Checksum $Path
}

function Write-FakeX64Pe([string]$Path) {
    $bytes = [byte[]]::new(128)
    $bytes[0] = 0x4d
    $bytes[1] = 0x5a
    [BitConverter]::GetBytes([int]0x40).CopyTo($bytes, 0x3c)
    $bytes[0x40] = 0x50
    $bytes[0x41] = 0x45
    $bytes[0x42] = 0
    $bytes[0x43] = 0
    $bytes[0x44] = 0x64
    $bytes[0x45] = 0x86
    [IO.File]::WriteAllBytes($Path, $bytes)
}

function New-HostEvidence(
    [string]$ProductName,
    [string]$DisplayVersion,
    [string]$Build,
    [int]$Dpi,
    [bool]$RequireCuda,
    [string]$ApplicationHash,
    [string]$NativeHash) {
    $status = if ($RequireCuda) { @('Native core ready', 'CUDA: fixture GPU') } else { @('Native core ready', 'CPU') }
    return [ordered]@{
        schemaVersion = 1
        capturedAtUtc = [DateTime]::UtcNow.ToString('O')
        passed = $true
        error = $null
        host = [ordered]@{
            computerName = 'FIXTURE'
            productName = $ProductName
            displayVersion = $DisplayVersion
            currentBuild = $Build
            ubr = 1
            osVersion = "$Build.1"
            processArchitecture = 'X64'
        }
        package = [ordered]@{
            version = '1.0.0'
            backend = 'cuda'
            runtimeIdentifier = 'win-x64'
            selfContained = $true
            sourceCommit = $sourceCommit
            validationLevel = 'synthetic-data-validated'
            applicationSha256 = $ApplicationHash
            nativeSha256 = $NativeHash
        }
        ui = [ordered]@{
            dpi = [int](96 * $Dpi / 100)
            dpiPercent = $Dpi
            expectedDpiPercent = $Dpi
            requireCuda = $RequireCuda
            perMonitorV2 = $true
            focusableWithoutNameCount = 0
            primaryCommandsVisible = [ordered]@{
                'Open TIFF / LSM' = $true
                'Run' = $true
                'Cancel' = $true
                'Export result + provenance' = $true
            }
            statusTexts = $status
            normalClose = $true
        }
    }
}

function New-ManualObservations([string]$Role, [string]$EvidenceHash) {
    return [ordered]@{
        schemaVersion = 1
        hostRole = $Role
        observedAtUtc = [DateTime]::UtcNow.ToString('O')
        observer = 'release-gate-fixture'
        hostEvidenceSha256 = $EvidenceHash
        checks = [ordered]@{
            representativeFileOpened = $true
            cancellationPreservedState = $true
            recoverableErrorPreservedState = $true
            orthogonalViewsUsableAtMinimumSize = $true
            exportAndReopenVerified = $true
        }
        notes = 'Synthetic release-gate script fixture only.'
    }
}

New-Item -ItemType Directory -Path $root | Out-Null
try {
    $packageRoot = Join-Path $root 'package'
    $docsRoot = Join-Path $packageRoot 'docs'
    New-Item -ItemType Directory -Path $docsRoot -Force | Out-Null
    Write-FakeX64Pe (Join-Path $packageRoot 'Dslt.App.exe')
    Write-FakeX64Pe (Join-Path $packageRoot 'dslt_core.dll')
    foreach ($runtime in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll')) {
        Set-Content -LiteralPath (Join-Path $packageRoot $runtime) -Value 'fixture' -Encoding ascii
    }
    Set-Content -LiteralPath (Join-Path $packageRoot 'COPYING.GPLv3') -Value @(
        'GNU GENERAL PUBLIC LICENSE',
        'Version 3, 29 June 2007',
        'END OF TERMS AND CONDITIONS') -Encoding utf8
    foreach ($file in @(
        'LEGACY-THIRD-PARTY-NOTICES.txt', 'DOTNET-LICENSE.txt',
        'DOTNET-THIRD-PARTY-NOTICES.txt', 'README.md')) {
        Set-Content -LiteralPath (Join-Path $packageRoot $file) -Value 'fixture' -Encoding utf8
    }
    $requiredDocs = @(
        'adaptive-threshold-spec.md', 'compatibility-matrix.md', 'dslt-algorithm-spec.md',
        'functional-spec.md', 'h-minima-spec.md', 'height-map-spec.md',
        'height-projection-spec.md', 'real-data-manifest.example.json',
        'provenance.md',
        'real-data-validation.md', 'release-policy.md', 'tiff-io-spec.md',
        'v1-release-evidence.md', 'windows-manual-observation.example.json',
        'ui-workflow.md', 'validation-policy.md', 'watershed-spec.md')
    foreach ($doc in $requiredDocs) {
        Set-Content -LiteralPath (Join-Path $docsRoot $doc) -Value 'fixture' -Encoding utf8
    }
    Set-Content -LiteralPath (Join-Path $docsRoot 'source-and-license.md') -Value @(
        'https://github.com/takashi310/DSLT',
        $legacyCommit) -Encoding utf8
    Set-Content -LiteralPath (Join-Path $docsRoot 'provenance.md') -Value @(
        'https://github.com/takashi310/DSLT',
        $legacyCommit) -Encoding utf8
    Write-Json (Join-Path $packageRoot 'BUILD-INFO.json') ([ordered]@{
        schemaVersion = 1
        packageVersion = '1.0.0'
        runtimeIdentifier = 'win-x64'
        selfContained = $true
        backend = 'cuda'
        sourceCommit = $sourceCommit
        legacyBaselineCommit = $legacyCommit
        validationLevel = 'synthetic-data-validated'
    })

    $archive = Join-Path $root 'dslt-workbench-1.0.0-win-x64-cuda.zip'
    Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $archive
    [void](Write-Checksum $archive)
    $applicationHash = (Get-FileHash -LiteralPath (Join-Path $packageRoot 'Dslt.App.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    $nativeHash = (Get-FileHash -LiteralPath (Join-Path $packageRoot 'dslt_core.dll') -Algorithm SHA256).Hash.ToLowerInvariant()

    $manifest = Join-Path $root 'manifest.json'
    $manifestCases = 0..4 | ForEach-Object {
        [ordered]@{
            id = "case-$_"
            acquisitionId = "acquisition-$_"
            dataClassification = 'representative-real'
            referenceKind = if ($_ % 2 -eq 0) { 'legacy' } else { 'expert' }
            inputFileSha256 = ('d' * 64)
            inputDecodedSha256 = ('e' * 64)
            referenceLabelsSha256 = ('f' * 64)
            candidateLabelsSha256 = ('a' * 64)
            candidateProvenanceSha256 = ('b' * 64)
        }
    }
    Write-Json $manifest ([ordered]@{
        schemaVersion = 2
        datasetName = 'fixture'
        candidateSourceCommit = $sourceCommit
        cases = @($manifestCases)
    })
    $manifestHash = (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash.ToLowerInvariant()
    $caseResults = 0..4 | ForEach-Object {
        [ordered]@{
            id = "case-$_"
            passed = $true
            metrics = [ordered]@{
                referenceObjectCount = 2
                candidateObjectCount = 2
                dice = 1.0
                volumeDifferenceFraction = 0.0
                hausdorff95Voxels = 0.0
            }
        }
    }
    $realReportPath = Join-Path $root 'real-data-report.json'
    [void](Write-JsonWithChecksum $realReportPath ([ordered]@{
        schemaVersion = 2
        datasetName = 'fixture'
        evaluatedAtUtc = [DateTime]::UtcNow.ToString('O')
        manifestSha256 = $manifestHash
        candidateSourceCommit = $sourceCommit
        thresholds = [ordered]@{
            minimumDice = 0.995
            maximumVolumeDifferenceFraction = 0.005
            maximumHausdorff95Voxels = 1.0
        }
        coverage = [ordered]@{
            caseCount = 5
            uniqueAcquisitionCount = 5
            hasSingleChannel = $true
            hasMultipleChannels = $true
            coveredVoxelTypes = @('float32', 'uint16', 'uint8')
            distinctZSpacingCount = 2
            passed = $true
        }
        cases = @($caseResults)
        releaseGatePassed = $true
    }))

    $cudaPath = Join-Path $root 'cuda.json'
    [void](Write-JsonWithChecksum $cudaPath ([ordered]@{
        schemaVersion = 1
        passed = $true
        sourceCommit = $sourceCommit
        gpu = @('fixture GPU, 1.0, 8192 MiB, 9.0')
        testBundle = [ordered]@{
            nativeTestsSha256 = ('b' * 64)
            nativeLibrarySha256 = ('c' * 64)
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
    }))

    $win10Path = Join-Path $root 'windows-10.json'
    $win10Hash = Write-JsonWithChecksum $win10Path (New-HostEvidence 'Windows 10 Pro' '22H2' '19045' 150 $false $applicationHash $nativeHash)
    $win10ManualPath = Join-Path $root 'windows-10.manual.json'
    [void](Write-JsonWithChecksum $win10ManualPath (New-ManualObservations 'windows-10-22h2-150' $win10Hash))

    $win11Path = Join-Path $root 'windows-11.json'
    $win11Hash = Write-JsonWithChecksum $win11Path (New-HostEvidence 'Windows 11 Pro' '25H2' '26200' 200 $true $applicationHash $nativeHash)
    $win11ManualPath = Join-Path $root 'windows-11.manual.json'
    $win11Manual = New-ManualObservations 'windows-11-200-cuda' $win11Hash
    [void](Write-JsonWithChecksum $win11ManualPath $win11Manual)

    $parameters = @{
        Archive = $archive
        Checksum = "$archive.sha256"
        RealDataManifest = $manifest
        RealDataReport = $realReportPath
        RealDataReportChecksum = "$realReportPath.sha256"
        CudaEvidence = $cudaPath
        CudaEvidenceChecksum = "$cudaPath.sha256"
        Windows10Evidence = $win10Path
        Windows10EvidenceChecksum = "$win10Path.sha256"
        Windows10Observations = $win10ManualPath
        Windows10ObservationsChecksum = "$win10ManualPath.sha256"
        Windows11Evidence = $win11Path
        Windows11EvidenceChecksum = "$win11Path.sha256"
        Windows11Observations = $win11ManualPath
        Windows11ObservationsChecksum = "$win11ManualPath.sha256"
        OutputPath = (Join-Path $root 'v1-gate.json')
    }
    & (Join-Path $PSScriptRoot 'verify-v1-release-evidence.ps1') @parameters
    $positive = Read-JsonFile $parameters.OutputPath 'Positive gate report'
    if ($positive.passed -ne $true) { throw 'Valid release-gate fixture did not pass.' }

    Set-Content -LiteralPath (Join-Path $docsRoot 'compatibility-matrix.md') -Value @(
        '| Legacy capability | Workbench contract | Status | Validation |',
        '| --- | --- | --- | --- |',
        '| Fixture | Fixture | scaffolded | Fixture |') -Encoding utf8
    $unfinishedRoot = Join-Path $root 'unfinished'
    New-Item -ItemType Directory -Path $unfinishedRoot | Out-Null
    $unfinishedArchive = Join-Path $unfinishedRoot 'dslt-workbench-1.0.0-win-x64-cuda.zip'
    Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $unfinishedArchive
    [void](Write-Checksum $unfinishedArchive)
    $parameters.Archive = $unfinishedArchive
    $parameters.Checksum = "$unfinishedArchive.sha256"
    $parameters.OutputPath = Join-Path $root 'v1-gate-unfinished.json'
    $unfinishedRejected = $false
    try {
        & (Join-Path $PSScriptRoot 'verify-v1-release-evidence.ps1') @parameters
    }
    catch {
        $unfinishedRejected = $true
    }
    if (-not $unfinishedRejected) { throw 'An unfinished compatibility row was not rejected.' }
    $unfinished = Read-JsonFile $parameters.OutputPath 'Unfinished compatibility gate report'
    if ($unfinished.passed -ne $false -or $unfinished.error -notmatch 'unfinished legacy capabilities') {
        throw 'Unfinished compatibility gate report did not preserve the rejection reason.'
    }
    $parameters.Archive = $archive
    $parameters.Checksum = "$archive.sha256"


    $win11Manual.checks.exportAndReopenVerified = $false
    [void](Write-JsonWithChecksum $win11ManualPath $win11Manual)
    $parameters.OutputPath = Join-Path $root 'v1-gate-negative.json'
    $rejected = $false
    try {
        & (Join-Path $PSScriptRoot 'verify-v1-release-evidence.ps1') @parameters
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) { throw 'Incomplete manual observations were not rejected.' }
    $negative = Read-JsonFile $parameters.OutputPath 'Negative gate report'
    if ($negative.passed -ne $false -or $negative.error -notmatch 'exportAndReopenVerified') {
        throw 'Negative gate report did not preserve the rejection reason.'
    }

    Write-Host 'V1.0 release evidence gate positive, unfinished-capability, and manual-tamper fixtures passed.'
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
