[CmdletBinding()]
param(
    [string]$Lock = (Join-Path $PSScriptRoot '..\validation\legacy-distribution.lock.json'),
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..\..'),
    [switch]$PassThru
)

$ErrorActionPreference = 'Stop'
$lockPath = (Resolve-Path -LiteralPath $Lock).Path
$repoRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$audit = Get-Content -LiteralPath $lockPath -Raw -Encoding utf8 | ConvertFrom-Json

function Assert-Equal($Actual, $Expected, [string]$Description) {
    if ($Actual -ne $Expected) {
        throw "$Description mismatch: expected '$Expected', received '$Actual'."
    }
}

function Get-PrefixSha256([string]$Path, [int]$Length) {
    if ($Length -le 0) { throw 'Prefix length must be positive.' }
    $stream = [IO.File]::OpenRead($Path)
    try {
        if ($stream.Length -lt $Length) {
            throw "File is shorter than the required prefix: $($stream.Length) < $Length."
        }
        $bytes = New-Object byte[] $Length
        $offset = 0
        while ($offset -lt $Length) {
            $read = $stream.Read($bytes, $offset, $Length - $offset)
            if ($read -eq 0) { throw 'Unexpected end of file while hashing the prefix.' }
            $offset += $read
        }
    }
    finally {
        $stream.Dispose()
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

Assert-Equal $audit.schemaVersion 1 'Legacy distribution lock schemaVersion'
Assert-Equal $audit.legacyBaseline.repository 'https://github.com/takashi310/DSLT' 'Legacy repository'
Assert-Equal $audit.legacyBaseline.commit 'aae2b3e5310fcaad4151a878ad65ed2a3fa29146' 'Legacy baseline commit'
Assert-Equal $audit.legacyBaseline.tag 'legacy-baseline-aae2b3e' 'Legacy baseline tag'
Assert-Equal $audit.historicalHost.baseUrl 'http://dslt.bot.kyoto-u.ac.jp/' 'Historical download host'
Assert-Equal $audit.archiveAudit.uniqueSuccessfulCaptureCount 1 'Unique Wayback capture count'
Assert-Equal $audit.archiveAudit.captureReplayStatus 'truncated-prefix' 'Wayback replay status'
Assert-Equal $audit.archiveAudit.commonCrawlStatus 'indeterminate-gateway-timeout' 'Common Crawl status'
Assert-Equal $audit.parameterEvidence.sourceBaselineAvailable $true 'Source parameter evidence availability'
Assert-Equal $audit.parameterEvidence.manualAvailable $true 'Manual parameter evidence availability'
Assert-Equal $audit.parameterEvidence.syntheticWorkbenchFixturesAvailable $true 'Synthetic parameter fixture availability'
Assert-Equal $audit.parameterEvidence.legacyRuntimeCaptureAvailable $false 'Legacy runtime parameter capture availability'
Assert-Equal $audit.parameterEvidence.status 'source-manual-synthetic-only' 'Parameter evidence status'
$parameterStatusPath = Join-Path $repoRoot ($audit.parameterEvidence.statusDocument -replace '/', '\')
if (-not (Test-Path -LiteralPath $parameterStatusPath -PathType Leaf)) {
    throw "Legacy runtime and parameter status document is missing: $parameterStatusPath"
}

$artifacts = @($audit.artifacts)
if ($artifacts.Count -ne 12) { throw "Expected 12 locked legacy artifacts, received $($artifacts.Count)." }
if (@($artifacts.name | Sort-Object -Unique).Count -ne $artifacts.Count) {
    throw 'Legacy artifact names must be unique.'
}
$expectedKinds = @{
    'installer-x64' = 2
    'installer-x86' = 2
    'manual' = 2
    'sample-tiff' = 2
    'sample-tiff-x86' = 2
    'sample-lsm' = 2
}
foreach ($entry in $expectedKinds.GetEnumerator()) {
    $actual = @($artifacts | Where-Object kind -eq $entry.Key).Count
    Assert-Equal $actual $entry.Value "Artifact kind '$($entry.Key)' count"
}
foreach ($artifact in $artifacts) {
    Assert-Equal $artifact.sourceUrl ($audit.historicalHost.baseUrl + $artifact.name) "Source URL for $($artifact.name)"
    if ($artifact.status -notin @('not-recovered', 'recovered-publisher-copy')) {
        throw "Invalid recovery status for $($artifact.name): $($artifact.status)."
    }
}

$recovered = @($artifacts | Where-Object status -eq 'recovered-publisher-copy')
if ($recovered.Count -ne 1 -or $recovered[0].name -ne 'DSLT_manual_v111.pdf') {
    throw 'Only the v1.11 publisher manual may be marked recovered in this lock revision.'
}

$manualPath = Join-Path $repoRoot ($audit.preservedManual.path -replace '/', '\')
if (-not (Test-Path -LiteralPath $manualPath -PathType Leaf)) {
    throw "Preserved manual is missing: $manualPath"
}
$manual = Get-Item -LiteralPath $manualPath
Assert-Equal $manual.Length ([long]$audit.preservedManual.size) 'Preserved manual size'
$manualHash = (Get-FileHash -LiteralPath $manualPath -Algorithm SHA256).Hash.ToLowerInvariant()
Assert-Equal $manualHash $audit.preservedManual.sha256 'Preserved manual SHA-256'
$prefixHash = Get-PrefixSha256 $manualPath ([int]$audit.preservedManual.waybackPrefixBytes)
Assert-Equal $prefixHash $audit.preservedManual.waybackPrefixSha256 'Preserved manual Wayback prefix SHA-256'
Assert-Equal $prefixHash $audit.archiveAudit.captureReplaySha256 'Archive replay SHA-256'
Assert-Equal $audit.preservedManual.size $audit.archiveAudit.crawlerReportedOriginalBytes 'Crawler-reported manual size'

$readmePath = Join-Path $repoRoot 'README.md'
$readme = Get-Content -LiteralPath $readmePath -Raw -Encoding utf8
foreach ($artifact in $artifacts) {
    if (-not $readme.Contains($artifact.name)) {
        throw "Legacy README no longer names the locked artifact: $($artifact.name)"
    }
}

$resolvedTag = (git -C $repoRoot rev-parse "$($audit.legacyBaseline.tag)^{commit}").Trim()
if ($LASTEXITCODE -ne 0) { throw "Could not resolve legacy baseline tag $($audit.legacyBaseline.tag)." }
Assert-Equal $resolvedTag $audit.legacyBaseline.commit 'Legacy baseline tag target'
$baselineObjects = @(git -C $repoRoot rev-list --objects $audit.legacyBaseline.commit)
if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate legacy baseline objects.' }
foreach ($artifact in $artifacts) {
    if ($baselineObjects -match ([regex]::Escape($artifact.name) + '$')) {
        throw "The lock says the artifact is external, but it is present in legacy Git history: $($artifact.name)"
    }
}

Assert-Equal $audit.releaseEvidence.recoveredInstallerCount 0 'Recovered installer count'
Assert-Equal $audit.releaseEvidence.recoveredSampleAcquisitionCount 0 'Recovered sample acquisition count'
Assert-Equal $audit.releaseEvidence.legacyRuntimeOracleAvailable $false 'Legacy runtime oracle availability'
Assert-Equal $audit.releaseEvidence.originalSampleLsmComparisonAvailable $false 'Original sample LSM comparison availability'
Assert-Equal $audit.releaseEvidence.v1ReleaseGateSatisfied $false 'Legacy distribution v1 gate'

$result = [ordered]@{
    schemaVersion = 1
    passed = $true
    lock = $lockPath
    legacyCommit = $resolvedTag
    artifactCount = $artifacts.Count
    recoveredArtifactCount = $recovered.Count
    recoveredSampleAcquisitionCount = 0
    manualSha256 = $manualHash
    waybackPrefixSha256 = $prefixHash
    legacyRuntimeOracleAvailable = $false
    parameterEvidenceStatus = $audit.parameterEvidence.status
    legacyRuntimeParameterCaptureAvailable = $false
    originalSampleLsmComparisonAvailable = $false
    v1ReleaseGateSatisfied = $false
}

if ($PassThru) { return [pscustomobject]$result }
$result | ConvertTo-Json -Depth 4
