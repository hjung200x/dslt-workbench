[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Archive,
    [Parameter(Mandatory)]
    [string]$Checksum,
    [Parameter(Mandatory)]
    [string]$RealDataManifest,
    [Parameter(Mandatory)]
    [string]$RealDataReport,
    [Parameter(Mandatory)]
    [string]$RealDataReportChecksum,
    [Parameter(Mandatory)]
    [string]$CudaEvidence,
    [Parameter(Mandatory)]
    [string]$CudaEvidenceChecksum,
    [Parameter(Mandatory)]
    [string]$Windows10Evidence,
    [Parameter(Mandatory)]
    [string]$Windows10EvidenceChecksum,
    [Parameter(Mandatory)]
    [string]$Windows10Observations,
    [Parameter(Mandatory)]
    [string]$Windows10ObservationsChecksum,
    [Parameter(Mandatory)]
    [string]$Windows11Evidence,
    [Parameter(Mandatory)]
    [string]$Windows11EvidenceChecksum,
    [Parameter(Mandatory)]
    [string]$Windows11Observations,
    [Parameter(Mandatory)]
    [string]$Windows11ObservationsChecksum,
    [Parameter(Mandatory)]
    [string]$OutputPath,
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$ExpectedVersion = '1.0.0',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$outputChecksumPath = "$outputFullPath.sha256"
$protectedInputs = @(
    $Archive, $Checksum, $RealDataManifest, $RealDataReport, $RealDataReportChecksum,
    $CudaEvidence, $CudaEvidenceChecksum,
    $Windows10Evidence, $Windows10EvidenceChecksum,
    $Windows10Observations, $Windows10ObservationsChecksum,
    $Windows11Evidence, $Windows11EvidenceChecksum,
    $Windows11Observations, $Windows11ObservationsChecksum)
foreach ($inputPath in $protectedInputs) {
    $inputFullPath = [IO.Path]::GetFullPath($inputPath)
    if ($inputFullPath.Equals($outputFullPath, [StringComparison]::OrdinalIgnoreCase) -or
        $inputFullPath.Equals($outputChecksumPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Release-gate output paths must not overwrite an input artifact.'
    }
}
if (((Test-Path -LiteralPath $outputFullPath) -or
     (Test-Path -LiteralPath $outputChecksumPath)) -and -not $Force) {
    throw "Release-gate report or checksum already exists; pass -Force to replace it: $outputFullPath"
}
$outputDirectory = Split-Path -Parent $outputFullPath
if (-not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

function Resolve-RequiredFile([string]$Path, [string]$Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description does not exist: $Path"
    }
    return (Resolve-Path -LiteralPath $Path).Path
}

function Assert-Sha256Sidecar([string]$Path, [string]$ChecksumPath, [string]$Description) {
    $resolvedPath = Resolve-RequiredFile $Path $Description
    $resolvedChecksum = Resolve-RequiredFile $ChecksumPath "$Description checksum"
    $checksumLine = (Get-Content -LiteralPath $resolvedChecksum -Raw -Encoding ascii).Trim()
    if ($checksumLine -notmatch '^([0-9a-fA-F]{64})\s+\*?(.+)$') {
        throw "$Description checksum must contain a SHA-256 hash and filename."
    }
    $expectedHash = $Matches[1].ToLowerInvariant()
    $expectedName = $Matches[2]
    $actualName = Split-Path -Leaf $resolvedPath
    if ($expectedName -ne $actualName) {
        throw "$Description checksum names '$expectedName' instead of '$actualName'."
    }
    $actualHash = (Get-FileHash -LiteralPath $resolvedPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "$Description SHA-256 does not match its checksum."
    }
    return $actualHash
}

function Read-JsonFile([string]$Path, [string]$Description) {
    try {
        return Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json
    }
    catch {
        throw "$Description is not valid JSON: $($_.Exception.Message)"
    }
}

function Assert-FullCommit([string]$Value, [string]$Description) {
    if ($Value -notmatch '^[0-9a-f]{40}$') {
        throw "$Description is not a full lowercase Git commit."
    }
}

function Assert-HostEvidence(
    [object]$Evidence,
    [string]$Role,
    [int]$ExpectedDpi,
    [string]$SourceCommit,
    [string]$PackageVersion,
    [string]$PackageBackend,
    [string]$ApplicationHash,
    [string]$NativeHash) {
    if ($Evidence.schemaVersion -ne 1 -or $Evidence.passed -ne $true) {
        throw "$Role automated host evidence did not pass schema 1."
    }
    if ($Evidence.host.processArchitecture -ne 'X64') {
        throw "$Role evidence was not captured by an x64 process."
    }
    $build = 0
    if (-not [int]::TryParse([string]$Evidence.host.currentBuild, [ref]$build)) {
        throw "$Role evidence has an invalid Windows build."
    }
    if ($Role -eq 'windows-10-22h2-150') {
        if ($Evidence.host.productName -notmatch 'Windows 10' -or
            $Evidence.host.displayVersion -ne '22H2' -or
            $build -ne 19045) {
            throw 'Windows 10 evidence must be genuine Windows 10 22H2 build 19045.'
        }
    }
    elseif ($build -lt 22000) {
        throw 'Windows 11 evidence must report build 22000 or newer.'
    }

    if ($Evidence.ui.dpiPercent -ne $ExpectedDpi -or
        $Evidence.ui.expectedDpiPercent -ne $ExpectedDpi -or
        $Evidence.ui.perMonitorV2 -ne $true -or
        $Evidence.ui.focusableWithoutNameCount -ne 0 -or
        $Evidence.ui.normalClose -ne $true) {
        throw "$Role evidence failed its DPI, accessibility, or normal-close contract."
    }
    foreach ($command in @('Open TIFF / LSM', 'Run', 'Cancel', 'Export result + provenance')) {
        $property = $Evidence.ui.primaryCommandsVisible.PSObject.Properties[$command]
        if ($null -eq $property -or $property.Value -ne $true) {
            throw "$Role evidence did not make '$command' reachable."
        }
    }
    if ($Role -eq 'windows-11-200-cuda') {
        $status = @($Evidence.ui.statusTexts) -join ' | '
        if ($Evidence.ui.requireCuda -ne $true -or $status -notmatch 'CUDA:') {
            throw 'Windows 11 evidence did not require and detect CUDA.'
        }
    }

    if ($Evidence.package.sourceCommit -ne $SourceCommit -or
        $Evidence.package.version -ne $PackageVersion -or
        $Evidence.package.backend -ne $PackageBackend -or
        $Evidence.package.runtimeIdentifier -ne 'win-x64' -or
        $Evidence.package.selfContained -ne $true -or
        $Evidence.package.applicationSha256 -ne $ApplicationHash -or
        $Evidence.package.nativeSha256 -ne $NativeHash) {
        throw "$Role evidence does not identify the exact release-candidate package."
    }
}

function Assert-ManualObservations(
    [object]$Observations,
    [string]$Role,
    [string]$HostEvidenceHash) {
    if ($Observations.schemaVersion -ne 1 -or $Observations.hostRole -ne $Role) {
        throw "$Role manual observations have the wrong schema or host role."
    }
    if ([string]::IsNullOrWhiteSpace([string]$Observations.observer)) {
        throw "$Role manual observations require an observer."
    }
    try {
        [void][DateTimeOffset]::Parse([string]$Observations.observedAtUtc)
    }
    catch {
        throw "$Role manual observations require a valid ISO-8601 timestamp."
    }
    if ($Observations.hostEvidenceSha256 -ne $HostEvidenceHash) {
        throw "$Role manual observations are not bound to the automated host evidence."
    }
    foreach ($check in @(
        'representativeFileOpened',
        'cancellationPreservedState',
        'recoverableErrorPreservedState',
        'orthogonalViewsUsableAtMinimumSize',
        'exportAndReopenVerified')) {
        $property = $Observations.checks.PSObject.Properties[$check]
        if ($null -eq $property -or $property.Value -ne $true) {
            throw "$Role manual observation '$check' was not confirmed."
        }
    }
}

$gate = [ordered]@{
    schemaVersion = 1
    evaluatedAtUtc = [DateTime]::UtcNow.ToString('O')
    passed = $false
    error = $null
    package = $null
    realData = $null
    cuda = $null
    windowsHosts = $null
}
$temporaryRoot = $null
$failure = $null
try {
    $archivePath = Resolve-RequiredFile $Archive 'Release-candidate archive'
    $checksumPath = Resolve-RequiredFile $Checksum 'Release-candidate checksum'
    $packageHash = Assert-Sha256Sidecar $archivePath $checksumPath 'Release-candidate archive'

    $LASTEXITCODE = 0
    & (Join-Path $PSScriptRoot 'verify-preview-package.ps1') -Archive $archivePath -Checksum $checksumPath
    if ($LASTEXITCODE -ne 0) { throw 'Release-candidate package verification failed.' }

    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("dslt-v1-gate-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    Expand-Archive -LiteralPath $archivePath -DestinationPath $temporaryRoot
    $buildInfo = Read-JsonFile (Join-Path $temporaryRoot 'BUILD-INFO.json') 'BUILD-INFO.json'
    if ($buildInfo.packageVersion -ne $ExpectedVersion -or $buildInfo.backend -ne 'cuda') {
        throw "v1.0 requires the exact $ExpectedVersion CUDA release-candidate package."
    }
    Assert-FullCommit $buildInfo.sourceCommit 'Package sourceCommit'
    $compatibilityMatrix = Get-Content -LiteralPath (Join-Path $temporaryRoot 'docs\compatibility-matrix.md') -Raw -Encoding utf8
    if ($compatibilityMatrix -match '\|\s*(scaffolded|pending-reference)\s*\|') {
        throw 'The packaged compatibility matrix still contains unfinished legacy capabilities.'
    }
    $applicationHash = (Get-FileHash -LiteralPath (Join-Path $temporaryRoot 'Dslt.App.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    $nativeHash = (Get-FileHash -LiteralPath (Join-Path $temporaryRoot 'dslt_core.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
    $gate.package = [ordered]@{
        archive = Split-Path -Leaf $archivePath
        sha256 = $packageHash
        version = $buildInfo.packageVersion
        backend = $buildInfo.backend
        sourceCommit = $buildInfo.sourceCommit
        applicationSha256 = $applicationHash
        nativeSha256 = $nativeHash
    }

    $manifestPath = Resolve-RequiredFile $RealDataManifest 'Real-data manifest'
    $reportPath = Resolve-RequiredFile $RealDataReport 'Real-data report'
    $reportHash = Assert-Sha256Sidecar $reportPath $RealDataReportChecksum 'Real-data report'
    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $realReport = Read-JsonFile $reportPath 'Real-data report'
    $realManifest = Read-JsonFile $manifestPath 'Real-data manifest'
    $manifestCases = @($realManifest.cases)
    if ($realManifest.schemaVersion -ne 2 -or
        $realManifest.candidateSourceCommit -ne $buildInfo.sourceCommit -or
        $realManifest.datasetName -ne $realReport.datasetName -or
        $manifestCases.Count -lt 5) {
        throw 'Real-data manifest does not match the report dataset and five-case contract.'
    }
    try {
        [void][DateTimeOffset]::Parse([string]$realReport.evaluatedAtUtc)
    }
    catch {
        throw 'Real-data report requires a valid evaluatedAtUtc timestamp.'
    }
    foreach ($manifestCase in $manifestCases) {
        if ($manifestCase.dataClassification -ne 'representative-real' -or
            $manifestCase.referenceKind -notin @('legacy', 'expert') -or
            [string]::IsNullOrWhiteSpace([string]$manifestCase.acquisitionId)) {
            throw "Manifest case '$($manifestCase.id)' is not declared as representative real data with an accepted reference."
        }
        foreach ($hashProperty in @(
            'inputFileSha256', 'inputDecodedSha256', 'referenceLabelsSha256',
            'referenceAcceptanceSha256', 'candidateLabelsSha256', 'candidateProvenanceSha256')) {
            if ([string]$manifestCase.$hashProperty -notmatch '^[0-9a-fA-F]{64}$') {
                throw "Manifest case '$($manifestCase.id)' has an invalid $hashProperty."
            }
        }
        if ([string]::IsNullOrWhiteSpace([string]$manifestCase.referenceAcceptancePath)) {
            throw "Manifest case '$($manifestCase.id)' has no reference acceptance record."
        }
    }
    if (@($manifestCases.acquisitionId | Sort-Object -Unique).Count -lt 5) {
        throw 'Real-data manifest requires five unique acquisitions.'
    }
    if ($realReport.schemaVersion -ne 2 -or $realReport.releaseGatePassed -ne $true -or
        $realReport.candidateSourceCommit -ne $buildInfo.sourceCommit -or
        $realReport.manifestSha256 -ne $manifestHash -or $realReport.coverage.passed -ne $true) {
        throw 'Real-data report is not a passing source-locked schema-2 report for the supplied manifest.'
    }
    if ([Math]::Abs([double]$realReport.thresholds.minimumDice - 0.995) -gt 1.0e-12 -or
        [Math]::Abs([double]$realReport.thresholds.maximumVolumeDifferenceFraction - 0.005) -gt 1.0e-12 -or
        [Math]::Abs([double]$realReport.thresholds.maximumHausdorff95Voxels - 1.0) -gt 1.0e-12) {
        throw 'Real-data report thresholds are weaker than the v1.0 contract.'
    }
    $realCases = @($realReport.cases)
    if ($realCases.Count -lt 5 -or $realReport.coverage.caseCount -ne $realCases.Count -or
        $realReport.coverage.uniqueAcquisitionCount -lt 5 -or
        $realReport.coverage.hasSingleChannel -ne $true -or
        $realReport.coverage.hasMultipleChannels -ne $true -or
        $realReport.coverage.distinctZSpacingCount -lt 2) {
        throw 'Real-data report does not satisfy the five-acquisition coverage contract.'
    }
    foreach ($voxelType in @('uint8', 'uint16', 'float32')) {
        if (@($realReport.coverage.coveredVoxelTypes) -notcontains $voxelType) {
            throw "Real-data report does not cover $voxelType."
        }
    }
    foreach ($case in $realCases) {
        $manifestCase = @($manifestCases | Where-Object id -eq $case.id)
        if ($manifestCase.Count -ne 1 -or
            $case.referenceAcceptanceSha256 -ne $manifestCase[0].referenceAcceptanceSha256 -or
            [string]::IsNullOrWhiteSpace([string]$case.referenceAcceptedBy) -or
            [string]::IsNullOrWhiteSpace([string]$case.referenceProtocolId)) {
            throw "Real-data case '$($case.id)' is not bound to a validated reference acceptance record."
        }
        if ($case.passed -ne $true -or $null -eq $case.metrics -or
            [double]$case.metrics.dice -lt 0.995 -or
            $case.metrics.referenceObjectCount -ne $case.metrics.candidateObjectCount -or
            [double]$case.metrics.volumeDifferenceFraction -gt 0.005 -or
            $null -eq $case.metrics.hausdorff95Voxels -or
            [double]$case.metrics.hausdorff95Voxels -gt 1.0) {
            throw "Real-data case '$($case.id)' does not satisfy every v1.0 metric."
        }
    }
    $gate.realData = [ordered]@{
        datasetName = $realReport.datasetName
        candidateSourceCommit = $realReport.candidateSourceCommit
        manifestSha256 = $manifestHash
        reportSha256 = $reportHash
        caseCount = $realCases.Count
        uniqueAcquisitionCount = $realReport.coverage.uniqueAcquisitionCount
    }

    $cudaPath = Resolve-RequiredFile $CudaEvidence 'CUDA parity evidence'
    $cudaHash = Assert-Sha256Sidecar $cudaPath $CudaEvidenceChecksum 'CUDA parity evidence'
    $cudaReport = Read-JsonFile $cudaPath 'CUDA parity evidence'
    $expectedOperations = @(
        'DSLT_OP_ADAPTIVE_THRESHOLD_2D', 'DSLT_OP_ADAPTIVE_THRESHOLD_3D',
        'DSLT_OP_CONNECTED_COMPONENTS', 'DSLT_OP_COPY', 'DSLT_OP_DEPTH_MAP',
        'DSLT_OP_DILATE_CUBE', 'DSLT_OP_DILATE_SPHERE', 'DSLT_OP_DSLT_SEGMENTATION',
        'DSLT_OP_DSLT_THRESHOLD', 'DSLT_OP_ERODE_CUBE', 'DSLT_OP_ERODE_SPHERE',
        'DSLT_OP_EXTRACT_XY', 'DSLT_OP_EXTRACT_YZ', 'DSLT_OP_EXTRACT_ZX',
        'DSLT_OP_H_MINIMA', 'DSLT_OP_HEIGHT_MAP', 'DSLT_OP_HEIGHT_PROJECTION',
        'DSLT_OP_Z_GRADIENT',
        'DSLT_OP_RESAMPLE_Z_AREA', 'DSLT_OP_RESAMPLE_Z_LANCZOS',
        'DSLT_OP_SMOOTH_GAUSSIAN', 'DSLT_OP_SMOOTH_MEAN', 'DSLT_OP_THRESHOLD_2D',
        'DSLT_OP_THRESHOLD_3D', 'DSLT_OP_THRESHOLD_SWEEP', 'DSLT_OP_WATERSHED',
        'DSLT_OP_WINDOW_LEVEL'
    )
    $cudaOperations = @($cudaReport.parity.publicOperations | Sort-Object -Unique)
    if ($cudaReport.schemaVersion -ne 1 -or $cudaReport.passed -ne $true -or
        $cudaReport.sourceCommit -ne $buildInfo.sourceCommit -or
        @($cudaReport.gpu).Count -lt 1 -or
        $cudaReport.parity.publicOperationCount -ne $expectedOperations.Count -or
        (Compare-Object $expectedOperations $cudaOperations) -or
        [double]$cudaReport.parity.floatAbsoluteTolerance -gt 1.0e-5 -or
        [double]$cudaReport.parity.floatRelativeTolerance -gt 1.0e-4 -or
        $cudaReport.parity.discreteVoxelExact -ne $true -or
        $cudaReport.parity.topologyExact -ne $true -or
        $cudaReport.parity.repeatedOperationCount -lt 100 -or
        $cudaReport.parity.deviceMemoryGrowthToleranceBytes -gt 1048576) {
        throw 'CUDA evidence does not prove full public-operation parity and memory stability for this source commit.'
    }
    $gate.cuda = [ordered]@{
        evidenceSha256 = $cudaHash
        gpu = @($cudaReport.gpu)
        publicOperationCount = $cudaOperations.Count
        nativeTestsSha256 = $cudaReport.testBundle.nativeTestsSha256
        nativeLibrarySha256 = $cudaReport.testBundle.nativeLibrarySha256
    }

    $win10Path = Resolve-RequiredFile $Windows10Evidence 'Windows 10 evidence'
    $win10Hash = Assert-Sha256Sidecar $win10Path $Windows10EvidenceChecksum 'Windows 10 evidence'
    $win10 = Read-JsonFile $win10Path 'Windows 10 evidence'
    Assert-HostEvidence $win10 'windows-10-22h2-150' 150 $buildInfo.sourceCommit $buildInfo.packageVersion $buildInfo.backend $applicationHash $nativeHash
    $win10ObservationsPath = Resolve-RequiredFile $Windows10Observations 'Windows 10 observations'
    $win10ObservationsHash = Assert-Sha256Sidecar $win10ObservationsPath $Windows10ObservationsChecksum 'Windows 10 observations'
    $win10Manual = Read-JsonFile $win10ObservationsPath 'Windows 10 observations'
    Assert-ManualObservations $win10Manual 'windows-10-22h2-150' $win10Hash

    $win11Path = Resolve-RequiredFile $Windows11Evidence 'Windows 11 evidence'
    $win11Hash = Assert-Sha256Sidecar $win11Path $Windows11EvidenceChecksum 'Windows 11 evidence'
    $win11 = Read-JsonFile $win11Path 'Windows 11 evidence'
    Assert-HostEvidence $win11 'windows-11-200-cuda' 200 $buildInfo.sourceCommit $buildInfo.packageVersion $buildInfo.backend $applicationHash $nativeHash
    $win11ObservationsPath = Resolve-RequiredFile $Windows11Observations 'Windows 11 observations'
    $win11ObservationsHash = Assert-Sha256Sidecar $win11ObservationsPath $Windows11ObservationsChecksum 'Windows 11 observations'
    $win11Manual = Read-JsonFile $win11ObservationsPath 'Windows 11 observations'
    Assert-ManualObservations $win11Manual 'windows-11-200-cuda' $win11Hash

    $gate.windowsHosts = [ordered]@{
        windows10 = [ordered]@{
            automatedEvidenceSha256 = $win10Hash
            manualObservationsSha256 = $win10ObservationsHash
            computerName = $win10.host.computerName
            build = "$($win10.host.currentBuild).$($win10.host.ubr)"
            dpiPercent = $win10.ui.dpiPercent
        }
        windows11 = [ordered]@{
            automatedEvidenceSha256 = $win11Hash
            manualObservationsSha256 = $win11ObservationsHash
            computerName = $win11.host.computerName
            build = "$($win11.host.currentBuild).$($win11.host.ubr)"
            dpiPercent = $win11.ui.dpiPercent
        }
    }
    $gate.passed = $true
}
catch {
    $failure = $_
    $gate.error = $_.Exception.Message
}
finally {
    if ($null -ne $temporaryRoot -and (Test-Path -LiteralPath $temporaryRoot)) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
    $gate | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $outputFullPath -Encoding utf8
    $gateHash = (Get-FileHash -LiteralPath $outputFullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $gateName = Split-Path -Leaf $outputFullPath
    Set-Content -LiteralPath $outputChecksumPath -Value "$gateHash  $gateName" -Encoding ascii
}

if ($null -ne $failure) { throw $failure }
Write-Host "V1.0 release evidence gate passed: $outputFullPath"
Write-Host "Evidence SHA-256: $gateHash"
