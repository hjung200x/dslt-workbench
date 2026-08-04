[CmdletBinding()]
param(
    [string]$Manifest = (Join-Path $PSScriptRoot '..\validation\public-lsm-interoperability.lock.json'),
    [string]$DataDirectory = (Join-Path $PSScriptRoot '..\validation\data\public-lsm'),
    [string]$Python = (Join-Path $PSScriptRoot '..\validation\data\python-czi-env\Scripts\python.exe'),
    [switch]$LockOnly
)

$ErrorActionPreference = 'Stop'
$manifestPath = (Resolve-Path -LiteralPath $Manifest).Path
$lock = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json
if ($lock.schemaVersion -ne 1 -or $lock.items.Count -ne 3) {
    throw 'The public LSM interoperability lock must use schemaVersion 1 and contain exactly three cases.'
}

$caseIds = @($lock.items.caseId)
if (($caseIds | Select-Object -Unique).Count -ne $caseIds.Count) { throw 'Public LSM case IDs must be unique.' }
foreach ($item in $lock.items) {
    if ($item.recordUrl -notmatch '^https://zenodo\.org/records/[0-9]+$' -or
        $item.downloadUrl -notmatch '^https://zenodo\.org/api/records/[0-9]+/files/.+/content$' -or
        $item.license -ne 'CC BY 4.0' -or
        $item.md5 -notmatch '^[0-9a-f]{32}$' -or
        $item.sha256 -notmatch '^[0-9a-f]{64}$' -or
        $item.decodedSha256 -notmatch '^[0-9a-f]{64}$' -or
        [long]$item.size -le 0 -or
        [IO.Path]::IsPathRooted([string]$item.relativePath) -or
        ([string]$item.relativePath -split '[/\\]' -contains '..')) {
        throw "Invalid public LSM lock metadata for $($item.caseId)."
    }
}

if ($LockOnly) {
    [pscustomobject]@{
        schemaVersion = 1
        passed = $true
        mode = 'lock-only'
        itemCount = $lock.items.Count
        totalDownloadBytes = [long](($lock.items | Measure-Object -Property size -Sum).Sum)
        representativeLeafGateSatisfied = $false
    } | ConvertTo-Json -Depth 4
    return
}

$dataRoot = (Resolve-Path -LiteralPath $DataDirectory).Path
$pythonPath = (Resolve-Path -LiteralPath $Python).Path

function Test-Close([double]$Left, [double]$Right) {
    return [Math]::Abs($Left - $Right) -le 1e-12 * [Math]::Max(1.0, [Math]::Max([Math]::Abs($Left), [Math]::Abs($Right)))
}

function Assert-Equal($Actual, $Expected, [string]$Label) {
    if ($Actual -ne $Expected) { throw "$Label mismatch: expected '$Expected', received '$Actual'." }
}

$independentJson = & $pythonPath (Join-Path $PSScriptRoot 'inspect-public-lsm.py') `
    --lock $manifestPath --data-directory $dataRoot
if ($LASTEXITCODE -ne 0) { throw "Independent tifffile inspection failed with exit code $LASTEXITCODE." }
$independent = $independentJson | ConvertFrom-Json
if (-not $independent.passed -or $independent.items.Count -ne $lock.items.Count) {
    throw 'Independent tifffile inspection did not pass every locked case.'
}

$prepareProject = Join-Path $PSScriptRoot '..\tools\Dslt.Validation.Prepare\Dslt.Validation.Prepare.csproj'
& dotnet build $prepareProject --configuration Release --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "Dslt.Validation.Prepare build failed with exit code $LASTEXITCODE." }

$results = foreach ($item in $lock.items) {
    $path = [IO.Path]::GetFullPath((Join-Path $dataRoot ([string]$item.relativePath)))
    $inspectionJson = & dotnet run --project $prepareProject --configuration Release --no-build -- `
        inspect-volume --input $path
    if ($LASTEXITCODE -ne 0) { throw "Workbench inspection failed for $($item.caseId)." }
    $inspection = $inspectionJson | ConvertFrom-Json

    Assert-Equal $inspection.fileSize ([long]$item.size) "$($item.caseId) file size"
    Assert-Equal $inspection.fileMd5 $item.md5 "$($item.caseId) file MD5"
    Assert-Equal $inspection.fileSha256 $item.sha256 "$($item.caseId) file SHA-256"
    Assert-Equal $inspection.canonicalAxes 'CZYX' "$($item.caseId) canonical axes"
    Assert-Equal $inspection.channels ([int]$item.canonicalShapeCzyx[0]) "$($item.caseId) channels"
    Assert-Equal $inspection.depth ([int]$item.canonicalShapeCzyx[1]) "$($item.caseId) depth"
    Assert-Equal $inspection.height ([int]$item.canonicalShapeCzyx[2]) "$($item.caseId) height"
    Assert-Equal $inspection.width ([int]$item.canonicalShapeCzyx[3]) "$($item.caseId) width"
    Assert-Equal $inspection.decodedSha256 $item.decodedSha256 "$($item.caseId) decoded SHA-256"
    $expectedVoxelType = if ($item.voxelType -eq 'uint8') { 'UnsignedInt8' } elseif ($item.voxelType -eq 'uint16') { 'UnsignedInt16' } else { throw "Unsupported locked voxel type: $($item.voxelType)" }
    Assert-Equal $inspection.voxelType $expectedVoxelType "$($item.caseId) voxel type"
    Assert-Equal $inspection.container 'LSM' "$($item.caseId) container"
    Assert-Equal $inspection.calibration.unitName 'um' "$($item.caseId) calibration unit"
    foreach ($axis in 'x','y','z') {
        $actual = [double]$inspection.calibration.("spacing$($axis.ToUpperInvariant())")
        $expected = [double]$item.calibrationUm.$axis
        if (-not (Test-Close $actual $expected)) { throw "$($item.caseId) spacing $axis mismatch: expected $expected, received $actual." }
    }
    Assert-Equal $inspection.channelMetadata.Count $item.channelMetadata.Count "$($item.caseId) channel metadata count"
    for ($index = 0; $index -lt $item.channelMetadata.Count; $index++) {
        foreach ($property in 'name','red','green','blue','alpha') {
            Assert-Equal $inspection.channelMetadata[$index].$property $item.channelMetadata[$index].$property "$($item.caseId) channel $index $property"
        }
    }
    Assert-Equal $inspection.timeStampsSeconds.Count $item.timeStampsSeconds.Count "$($item.caseId) timestamp count"
    for ($index = 0; $index -lt $item.timeStampsSeconds.Count; $index++) {
        if (-not (Test-Close ([double]$inspection.timeStampsSeconds[$index]) ([double]$item.timeStampsSeconds[$index]))) {
            throw "$($item.caseId) timestamp $index mismatch."
        }
    }

    [pscustomobject]@{
        caseId = $item.caseId
        dimensionsCzyx = @($inspection.channels, $inspection.depth, $inspection.height, $inspection.width)
        voxelType = $inspection.voxelType
        decodedSha256 = $inspection.decodedSha256
        independentMatch = ($independent.items | Where-Object caseId -eq $item.caseId).decodedSha256 -eq $inspection.decodedSha256
        passed = $true
    }
}

[pscustomobject]@{
    schemaVersion = 1
    passed = $true
    independentDecoder = $independent.decoder
    workbenchInspector = 'Dslt.Validation.Prepare inspect-volume'
    itemCount = $results.Count
    items = $results
} | ConvertTo-Json -Depth 6
