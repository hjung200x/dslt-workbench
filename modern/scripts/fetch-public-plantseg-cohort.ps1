[CmdletBinding()]
param(
    [string]$Manifest = (Join-Path $PSScriptRoot '..\validation\public-plantseg-cohort.lock.json'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\validation\data\public-plantseg-hdf5'),
    [string[]]$AcquisitionId,
    [switch]$IncludeSpare,
    [switch]$Repair
)

$ErrorActionPreference = 'Stop'
$manifestPath = (Resolve-Path -LiteralPath $Manifest).Path
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$outputPrefix = $outputRoot.TrimEnd('\') + '\'
$lock = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json
if ($lock.schemaVersion -ne 1 -or -not $lock.items) {
    throw 'The PlantSeg cohort lock must use schemaVersion 1 and contain items.'
}

function Assert-SafeChild([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the cohort output directory: $full"
    }
    return $full
}

function Assert-LockedFile([string]$Path, $Item) {
    $file = Get-Item -LiteralPath $Path
    if ($file.Length -ne [long]$Item.size) {
        throw "Size mismatch for $($Item.name): expected $($Item.size), received $($file.Length)."
    }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Item.sha256) {
        throw "SHA-256 mismatch for $($Item.name): expected $($Item.sha256), received $actual."
    }
    return $actual
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

$results = foreach ($item in $selected) {
    if ($item.sha256 -notmatch '^[0-9a-f]{64}$' -or [long]$item.size -le 0) {
        throw "Invalid lock metadata for $($item.acquisitionId)."
    }
    $destination = Assert-SafeChild (Join-Path $outputRoot $item.name)
    if (Test-Path -LiteralPath $destination) {
        try {
            $hash = Assert-LockedFile $destination $item
            [pscustomobject]@{
                acquisitionId = $item.acquisitionId
                path = $destination
                size = [long]$item.size
                sha256 = $hash
                status = 'verified-existing'
            }
            continue
        }
        catch {
            if (-not $Repair) { throw }
            Remove-Item -LiteralPath $destination -Force
        }
    }

    $partial = Assert-SafeChild "$destination.partial"
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    $url = "https://files.de-1.osf.io/v1/resources/$($item.nodeId)/providers/osfstorage/$($item.fileId)?action=download"
    try {
        & curl.exe --location --fail --silent --show-error --retry 12 --retry-all-errors `
            --retry-delay 10 --user-agent 'DSLT-Workbench-validation/1.0' `
            --output $partial $url
        if ($LASTEXITCODE -ne 0) { throw "curl failed with exit code $LASTEXITCODE." }
        $hash = Assert-LockedFile $partial $item
        Move-Item -LiteralPath $partial -Destination $destination -Force
        [pscustomobject]@{
            acquisitionId = $item.acquisitionId
            path = $destination
            size = [long]$item.size
            sha256 = $hash
            status = 'downloaded'
        }
    }
    finally {
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    }
}

$results | ConvertTo-Json -Depth 4
