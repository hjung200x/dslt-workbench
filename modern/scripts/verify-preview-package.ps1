[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Archive,
    [Parameter(Mandatory)]
    [string]$Checksum
)

$ErrorActionPreference = 'Stop'
$archivePath = (Resolve-Path -LiteralPath $Archive).Path
$checksumPath = (Resolve-Path -LiteralPath $Checksum).Path
$archiveName = Split-Path $archivePath -Leaf
if ($archiveName -notmatch '^dslt-workbench-(.+)-win-x64-(cpu|cuda)\.zip$') {
    throw 'Archive filename must identify the package version, win-x64 runtime, and CPU or CUDA backend.'
}
$filenameVersion = $Matches[1]
$filenameBackend = $Matches[2]
$checksumLine = (Get-Content -LiteralPath $checksumPath -Raw -Encoding ascii).Trim()
if ($checksumLine -notmatch '^([0-9a-fA-F]{64})\s+\*?(.+)$') {
    throw 'Checksum file must contain a SHA-256 hash and archive filename.'
}
$expectedHash = $Matches[1].ToLowerInvariant()
$expectedName = $Matches[2]
if ($expectedName -ne $archiveName) {
    throw "Checksum filename '$expectedName' does not match the archive."
}
$actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) { throw 'Archive SHA-256 does not match the checksum file.' }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $seenEntries = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $zip.Entries) {
        $normalizedName = $entry.FullName.Replace('\', '/')
        $segments = $normalizedName.Split('/')
        if ([IO.Path]::IsPathRooted($entry.FullName) -or $segments -contains '..') {
            throw "Archive contains an unsafe path: $($entry.FullName)"
        }
        if (-not $seenEntries.Add($normalizedName)) {
            throw "Archive contains a duplicate case-insensitive path: $($entry.FullName)"
        }
    }
}
finally {
    $zip.Dispose()
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("dslt-package-verify-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    Expand-Archive -LiteralPath $archivePath -DestinationPath $temporaryRoot
    $requiredFiles = @(
        'Dslt.App.exe',
        'dslt_core.dll',
        'coreclr.dll',
        'hostfxr.dll',
        'hostpolicy.dll',
        'COPYING.GPLv3',
        'LEGACY-THIRD-PARTY-NOTICES.txt',
        'DOTNET-LICENSE.txt',
        'DOTNET-THIRD-PARTY-NOTICES.txt',
        'README.md',
        'docs\source-and-license.md',
        'docs\provenance.md',
        'docs\compatibility-matrix.md',
        'docs\dslt-algorithm-spec.md',
        'docs\functional-spec.md',
        'docs\release-policy.md',
        'docs\tiff-io-spec.md',
        'docs\ui-workflow.md',
        'docs\validation-policy.md',
        'BUILD-INFO.json'
    )
    foreach ($relativePath in $requiredFiles) {
        $path = Join-Path $temporaryRoot $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Package is missing required file: $relativePath"
        }
        if ((Get-Item -LiteralPath $path).Length -eq 0) { throw "Package contains an empty file: $relativePath" }
    }

    $gpl = Get-Content -LiteralPath (Join-Path $temporaryRoot 'COPYING.GPLv3') -Raw -Encoding utf8
    if ($gpl -notmatch 'GNU GENERAL PUBLIC LICENSE' -or
        $gpl -notmatch 'Version 3, 29 June 2007' -or
        $gpl -notmatch 'END OF TERMS AND CONDITIONS') {
        throw 'COPYING.GPLv3 does not contain the complete GPLv3 markers.'
    }
    $provenance = Get-Content -LiteralPath (Join-Path $temporaryRoot 'docs\provenance.md') -Raw -Encoding utf8
    if ($provenance -notmatch 'takashi310/DSLT' -or
        $provenance -notmatch 'aae2b3e5310fcaad4151a878ad65ed2a3fa29146') {
        throw 'Package provenance does not identify the upstream repository and baseline commit.'
    }

    $buildInfo = Get-Content -LiteralPath (Join-Path $temporaryRoot 'BUILD-INFO.json') -Raw -Encoding utf8 | ConvertFrom-Json
    if ($buildInfo.runtimeIdentifier -ne 'win-x64' -or $buildInfo.selfContained -ne $true) {
        throw 'BUILD-INFO.json does not declare a self-contained win-x64 package.'
    }
    if ($buildInfo.packageVersion -ne $filenameVersion -or $buildInfo.backend -ne $filenameBackend) {
        throw 'BUILD-INFO.json version or backend does not match the archive filename.'
    }
    if ($buildInfo.validationLevel -ne 'synthetic-data-validated') {
        throw 'Preview package validation level is missing or invalid.'
    }
    if ($buildInfo.sourceCommit -notmatch '^[0-9a-f]{40}$' -or
        $buildInfo.legacyBaselineCommit -ne 'aae2b3e5310fcaad4151a878ad65ed2a3fa29146') {
        throw 'BUILD-INFO.json source or legacy commit is invalid.'
    }

    function Get-PeMachine([string]$Path) {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $reader = [IO.BinaryReader]::new($stream)
        try {
            if ($reader.ReadUInt16() -ne 0x5A4D) { throw "$Path is not a PE executable." }
            $stream.Position = 0x3C
            $peOffset = $reader.ReadInt32()
            if ($peOffset -lt 0 -or $peOffset -gt $stream.Length - 6) { throw "$Path has an invalid PE header." }
            $stream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550) { throw "$Path has an invalid PE signature." }
            return $reader.ReadUInt16()
        }
        finally {
            $reader.Dispose()
            $stream.Dispose()
        }
    }
    foreach ($binary in @('Dslt.App.exe', 'dslt_core.dll')) {
        if ((Get-PeMachine (Join-Path $temporaryRoot $binary)) -ne 0x8664) {
            throw "$binary is not an x64 PE binary."
        }
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Host "Verified package: $archivePath"
Write-Host "SHA-256: $actualHash"
