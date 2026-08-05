[CmdletBinding()]
param(
    [string]$Record = (Join-Path $PSScriptRoot '..\validation\public-clsm-leaf-preflight.json'),
    [string]$DataDirectory = (Join-Path $PSScriptRoot '..\validation\data\public-clsm-leaf'),
    [switch]$LockOnly
)

$ErrorActionPreference = 'Stop'
$recordPath = (Resolve-Path -LiteralPath $Record).Path
$audit = Get-Content -LiteralPath $recordPath -Raw -Encoding utf8 | ConvertFrom-Json
if ($audit.schemaVersion -ne 1 -or $audit.classification -ne 'input-only-leaf-preflight') {
    throw 'The public CLSM leaf preflight must use schemaVersion 1 and input-only-leaf-preflight classification.'
}
if ($audit.inputVolume.sha256 -notmatch '^[0-9a-f]{64}$' -or
    $audit.inputVolume.decodedSha256 -notmatch '^[0-9a-f]{64}$' -or
    $audit.companionMesh.sha256 -notmatch '^[0-9a-f]{64}$') {
    throw 'The public CLSM leaf preflight contains an invalid SHA-256 value.'
}
if ($audit.source.bundleAttributionStatus -ne 'probable-not-confirmed-from-retained-download-metadata' -or
    $audit.companionMesh.isVoxelAligned3dLabelVolume -ne $false -or
    $audit.releaseAdmission.acceptedLegacyOrExpertVoxelLabelsAvailable -ne $false -or
    $audit.releaseAdmission.numericMetricGateRunnable -ne $false -or
    $audit.releaseAdmission.v1GateCaseCount -ne 0) {
    throw 'The public CLSM leaf preflight must remain fail-closed until source custody and voxel labels are supplied.'
}

if ($LockOnly) {
    [pscustomobject]@{
        schemaVersion = 1
        passed = $true
        mode = 'lock-only'
        acquisitionId = $audit.acquisitionId
        labelKind = $audit.companionMesh.kind
        v1GateCaseCount = 0
    } | ConvertTo-Json -Depth 4
    return
}

$dataRoot = (Resolve-Path -LiteralPath $DataDirectory).Path
$inputPath = Join-Path $dataRoot $audit.inputVolume.name
$meshPath = Join-Path $dataRoot $audit.companionMesh.name
foreach ($path in $inputPath,$meshPath) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Public CLSM leaf evidence is missing: $path" }
}
if ((Get-Item -LiteralPath $inputPath).Length -ne [long]$audit.inputVolume.size -or
    (Get-FileHash -LiteralPath $inputPath -Algorithm MD5).Hash.ToLowerInvariant() -ne $audit.inputVolume.md5 -or
    (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $audit.inputVolume.sha256) {
    throw 'Public CLSM leaf input file does not match its locked identity.'
}
if ((Get-Item -LiteralPath $meshPath).Length -ne [long]$audit.companionMesh.size -or
    (Get-FileHash -LiteralPath $meshPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $audit.companionMesh.sha256) {
    throw 'Public CLSM leaf mesh does not match its locked identity.'
}
$meshStream = [IO.File]::OpenRead($meshPath)
try {
    $header = New-Object byte[] 9
    if ($meshStream.Read($header, 0, $header.Length) -ne $header.Length) { throw 'Public CLSM companion header is truncated.' }
}
finally { $meshStream.Dispose() }
if ([Text.Encoding]::ASCII.GetString($header) -ne 'MGXM 2.0 ') { throw 'Public CLSM companion does not have the locked MGXM 2.0 header.' }

$prepareProject = Join-Path $PSScriptRoot '..\tools\Dslt.Validation.Prepare\Dslt.Validation.Prepare.csproj'
& dotnet build $prepareProject --configuration Release --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Dslt.Validation.Prepare build failed.' }
$inspectionJson = & dotnet run --project $prepareProject --configuration Release --no-build -- inspect-volume --input $inputPath
if ($LASTEXITCODE -ne 0) { throw 'Workbench leaf TIFF inspection failed.' }
$inspection = $inspectionJson | ConvertFrom-Json
if ($inspection.fileSha256 -ne $audit.inputVolume.sha256 -or
    $inspection.decodedSha256 -ne $audit.inputVolume.decodedSha256 -or
    $inspection.voxelType -ne 'UnsignedInt16' -or
    $inspection.container -ne 'TIFF' -or
    $inspection.width -ne $audit.inputVolume.dimensions.width -or
    $inspection.height -ne $audit.inputVolume.dimensions.height -or
    $inspection.depth -ne $audit.inputVolume.dimensions.depth -or
    $inspection.channels -ne $audit.inputVolume.dimensions.channels) {
    throw 'Workbench leaf TIFF inspection does not match the preflight record.'
}

[pscustomobject]@{
    schemaVersion = 1
    passed = $true
    acquisitionId = $audit.acquisitionId
    dimensions = $audit.inputVolume.dimensions
    voxelType = $inspection.voxelType
    decodedSha256 = $inspection.decodedSha256
    companionKind = $audit.companionMesh.kind
    v1GateCaseCount = 0
} | ConvertTo-Json -Depth 5
