[CmdletBinding()]
param(
    [string]$Record = (Join-Path $PSScriptRoot '..\validation\public-plantseg-reference-suitability.lock.json'),
    [string]$DataDirectory = (Join-Path $PSScriptRoot '..\validation\data\public-plantseg-hdf5\crops'),
    [switch]$LockOnly
)

$ErrorActionPreference = 'Stop'
$recordPath = (Resolve-Path -LiteralPath $Record).Path
$auditLock = Get-Content -LiteralPath $recordPath -Raw -Encoding utf8 | ConvertFrom-Json
$cases = @($auditLock.cases)
if ($auditLock.schemaVersion -ne 1 -or
    $auditLock.classification -ne 'reference-structure-preflight' -or
    $cases.Count -ne 5) {
    throw 'PlantSeg reference suitability lock must contain five schema-1 preflight cases.'
}
if ($auditLock.metricContract.hausdorff95 -notmatch 'positive-label transitions are ignored' -or
    $auditLock.releaseAdmission.structurallyCompatibleWithoutProtocolReview -ne $false -or
    $auditLock.releaseAdmission.acceptedLegacyOrExpertLeafReferencesAvailable -ne $false -or
    $auditLock.releaseAdmission.v1GateCaseCount -ne 0) {
    throw 'PlantSeg reference suitability lock must remain fail-closed for v1.0 admission.'
}
foreach ($case in $cases) {
    if ($case.sha256 -notmatch '^[0-9a-f]{64}$' -or
        $case.boundaryRepresentation -ne 'TouchingInstances' -or
        $case.requiresReferenceProtocolReview -ne $true -or
        [long]$case.differentForegroundLabelFaceCount -le 0) {
        throw "PlantSeg reference suitability case '$($case.id)' does not preserve the touching-instance finding."
    }
}

if ($LockOnly) {
    [pscustomobject]@{
        schemaVersion = 1
        passed = $true
        mode = 'lock-only'
        caseCount = $cases.Count
        touchingInstanceCaseCount = @($cases | Where-Object boundaryRepresentation -eq 'TouchingInstances').Count
        v1GateCaseCount = 0
    } | ConvertTo-Json -Depth 4
    return
}

$dataRoot = (Resolve-Path -LiteralPath $DataDirectory).Path
$prepareProject = Join-Path $PSScriptRoot '..\tools\Dslt.Validation.Prepare\Dslt.Validation.Prepare.csproj'
& dotnet build $prepareProject --configuration Release --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Dslt.Validation.Prepare build failed.' }

foreach ($case in $cases) {
    $referencePath = Join-Path $dataRoot $case.file
    if (-not (Test-Path -LiteralPath $referencePath -PathType Leaf)) {
        throw "PlantSeg reference is missing: $referencePath"
    }
    $actualHash = (Get-FileHash -LiteralPath $referencePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $case.sha256) {
        throw "PlantSeg reference '$($case.id)' does not match its locked identity."
    }
    $json = & dotnet run --project $prepareProject --configuration Release --no-build -- `
        audit-reference --input $referencePath --background 0
    if ($LASTEXITCODE -ne 0) { throw "Reference audit failed for '$($case.id)'." }
    $actual = $json | ConvertFrom-Json
    if ($actual.sourceFileSha256 -ne $case.sha256 -or
        $actual.width -ne $case.dimensions.width -or
        $actual.height -ne $case.dimensions.height -or
        $actual.depth -ne $case.dimensions.depth -or
        [long]$actual.foregroundVoxelCount -ne [long]$case.foregroundVoxelCount -or
        [int]$actual.distinctForegroundLabelCount -ne [int]$case.distinctForegroundLabelCount -or
        [long]$actual.backgroundVoxelCount -ne [long]$case.backgroundVoxelCount -or
        [long]$actual.exteriorConnectedBackgroundVoxelCount -ne [long]$case.exteriorConnectedBackgroundVoxelCount -or
        [long]$actual.differentForegroundLabelFaceCount -ne [long]$case.differentForegroundLabelFaceCount -or
        [long]$actual.differentForegroundLabelInterfaceVoxelCount -ne [long]$case.differentForegroundLabelInterfaceVoxelCount -or
        $actual.boundaryRepresentation -ne 'TouchingInstances' -or
        $actual.requiresReferenceProtocolReview -ne $true) {
        throw "Reference audit for '$($case.id)' does not match the locked result."
    }
}

[pscustomobject]@{
    schemaVersion = 1
    passed = $true
    caseCount = $cases.Count
    touchingInstanceCaseCount = $cases.Count
    v1GateCaseCount = 0
} | ConvertTo-Json -Depth 4
