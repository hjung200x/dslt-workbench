[CmdletBinding()]
param(
    [string]$Manifest = (Join-Path $PSScriptRoot '..\validation\public-lsm-interoperability.lock.json'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\validation\data\public-lsm'),
    [string[]]$CaseId,
    [switch]$Repair
)

$ErrorActionPreference = 'Stop'
$manifestPath = (Resolve-Path -LiteralPath $Manifest).Path
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$outputPrefix = $outputRoot.TrimEnd('\') + '\'
$lock = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json
if ($lock.schemaVersion -ne 1 -or -not $lock.items) {
    throw 'The public LSM lock must use schemaVersion 1 and contain items.'
}

function Resolve-SafeChild([string]$RelativePath) {
    if ([IO.Path]::IsPathRooted($RelativePath) -or $RelativePath -split '[/\\]' -contains '..') {
        throw "Unsafe relative path in public LSM lock: $RelativePath"
    }
    $full = [IO.Path]::GetFullPath((Join-Path $outputRoot $RelativePath))
    if (-not $full.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the public LSM directory: $full"
    }
    return $full
}

function Assert-LockedFile([string]$Path, $Item) {
    $file = Get-Item -LiteralPath $Path
    if ($file.Length -ne [long]$Item.size) {
        throw "Size mismatch for $($Item.name): expected $($Item.size), received $($file.Length)."
    }
    $md5 = (Get-FileHash -LiteralPath $Path -Algorithm MD5).Hash.ToLowerInvariant()
    $sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($md5 -ne $Item.md5 -or $sha256 -ne $Item.sha256) {
        throw "Digest mismatch for $($Item.name)."
    }
    return $sha256
}

$selected = @($lock.items | Where-Object { -not $CaseId -or $CaseId -contains $_.caseId })
if ($selected.Count -eq 0) { throw 'No public LSM item matched the requested case IDs.' }
if ($CaseId) {
    $missing = @($CaseId | Where-Object { $_ -notin $selected.caseId })
    if ($missing.Count -ne 0) { throw "Unknown public LSM case ID: $($missing -join ', ')" }
}

$results = foreach ($item in $selected) {
    if ($item.md5 -notmatch '^[0-9a-f]{32}$' -or $item.sha256 -notmatch '^[0-9a-f]{64}$' -or [long]$item.size -le 0) {
        throw "Invalid lock metadata for $($item.caseId)."
    }
    $destination = Resolve-SafeChild ([string]$item.relativePath)
    $parent = Split-Path -Parent $destination
    New-Item -ItemType Directory -Path $parent -Force | Out-Null

    if (Test-Path -LiteralPath $destination) {
        try {
            $hash = Assert-LockedFile $destination $item
            [pscustomobject]@{ caseId = $item.caseId; path = $destination; size = [long]$item.size; sha256 = $hash; status = 'verified-existing' }
            continue
        }
        catch {
            if (-not $Repair) { throw }
            Remove-Item -LiteralPath $destination -Force
        }
    }

    $partial = Resolve-SafeChild (([string]$item.relativePath) + '.partial')
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    try {
        & curl.exe --location --fail --silent --show-error --retry 12 --retry-all-errors `
            --retry-delay 10 --user-agent 'DSLT-Workbench-validation/1.0' `
            --output $partial ([string]$item.downloadUrl)
        if ($LASTEXITCODE -ne 0) { throw "curl failed with exit code $LASTEXITCODE." }
        $hash = Assert-LockedFile $partial $item
        Move-Item -LiteralPath $partial -Destination $destination -Force
        [pscustomobject]@{ caseId = $item.caseId; path = $destination; size = [long]$item.size; sha256 = $hash; status = 'downloaded' }
    }
    finally {
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    }
}

$results | ConvertTo-Json -Depth 4
