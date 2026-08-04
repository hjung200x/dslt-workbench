[CmdletBinding()]
param(
    [string]$Manifest = (Join-Path $PSScriptRoot '..\validation\public-plantseg-cohort.lock.json'),
    [string]$SourceDirectory = (Join-Path $PSScriptRoot '..\validation\data\public-plantseg-hdf5'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\validation\data\public-plantseg-hdf5\converted'),
    [string[]]$AcquisitionId,
    [switch]$IncludeSpare,
    [switch]$Force,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$modernRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$manifestPath = (Resolve-Path -LiteralPath $Manifest).Path
$sourceRoot = (Resolve-Path -LiteralPath $SourceDirectory).Path
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$lock = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json
if ($lock.schemaVersion -ne 1 -or -not $lock.items) {
    throw 'The PlantSeg cohort lock must use schemaVersion 1 and contain items.'
}

$selected = @($lock.items | Where-Object {
    ($IncludeSpare -or $_.role -eq 'gate-candidate') -and
    (-not $AcquisitionId -or $AcquisitionId -contains $_.acquisitionId)
})
if ($selected.Count -eq 0) { throw 'No cohort item matched the requested role and acquisition filters.' }
if ($AcquisitionId) {
    $missing = @($AcquisitionId | Where-Object { $_ -notin $selected.acquisitionId })
    if ($missing.Count -ne 0) { throw "Unknown or excluded acquisition ID: $($missing -join ', ')" }
}

$project = Join-Path $modernRoot 'tools\Dslt.Validation.Prepare\Dslt.Validation.Prepare.csproj'
$results = foreach ($item in $selected) {
    $source = Join-Path $sourceRoot $item.name
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Locked source is missing: $source. Run fetch-public-plantseg-cohort.ps1 first."
    }
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sourceHash -ne $item.sha256) {
        throw "Locked source hash mismatch for $($item.acquisitionId)."
    }

    $stem = [IO.Path]::GetFileNameWithoutExtension($item.name)
    $voxelType = [string]$item.representation.voxelType
    $channels = [int]$item.representation.channels
    $input = Join-Path $outputRoot "$stem.input.$voxelType.${channels}c.tif"
    $reference = Join-Path $outputRoot "$stem.reference.signed.tif"
    $arguments = @(
        'run', '--project', $project, '--configuration', 'Release'
    )
    if ($NoBuild) { $arguments += '--no-build' }
    $arguments += @(
        '--', 'import-plantseg-hdf5',
        '--input', $source,
        '--output-volume', $input,
        '--output-labels', $reference,
        '--spacing-x', ([string]$item.spacingUm.x),
        '--spacing-y', ([string]$item.spacingUm.y),
        '--spacing-z', ([string]$item.spacingUm.z),
        '--unit', 'um',
        '--voxel-type', $voxelType,
        '--channels', ([string]$channels)
    )
    if ($Force) { $arguments += '--force' }
    $json = (& dotnet @arguments) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) {
        throw "PlantSeg conversion failed for $($item.acquisitionId): $json"
    }
    $converted = $json | ConvertFrom-Json
    [pscustomobject]@{
        acquisitionId = $item.acquisitionId
        role = $item.role
        organ = $item.organ
        inputVolumePath = $converted.inputVolumePath
        referenceLabelsPath = $converted.referenceLabelsPath
        width = $converted.width
        height = $converted.height
        depth = $converted.depth
        channels = $converted.channels
        voxelType = $converted.voxelType
        distinctLabelCount = $converted.distinctLabelCount
        inputFileSha256 = $converted.inputFileSha256
        referenceFileSha256 = $converted.referenceFileSha256
    }
}

$results | ConvertTo-Json -Depth 4
