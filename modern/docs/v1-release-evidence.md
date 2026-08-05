# V1.0 release evidence gate

`verify-v1-release-evidence.ps1` is the fail-closed approval gate for the v1.0
Windows x64 CUDA release candidate. A passing preview or one successful host is
not sufficient. The script binds every required artifact to the package source
commit and writes a machine-readable JSON decision plus an adjacent SHA-256
file.

## Build the exact candidate

From a clean checkout whose compatibility matrix has no `scaffolded` or
`pending-reference` rows, build the CUDA candidate:

```powershell
.\modern\scripts\package-preview.ps1 `
  -Version 1.0.0 `
  -Cuda `
  -ReleaseCandidate
```

The package is written below `modern/artifacts/release-candidate`. It remains
marked `synthetic-data-validated` until the external v1 evidence gate passes;
the candidate label must not be presented as an approved release by itself.

## Required evidence

Collect all of these files without editing them after their checksums are
written:

- the `1.0.0` self-contained `win-x64-cuda` ZIP and checksum;
- the schema-2 representative-real manifest whose `candidateSourceCommit`
  equals the package commit, a passing source-locked `Dslt.Validation` report,
  and a checksum for that report; every case must include a SHA-256-locked
  reference acceptance record with reviewer and protocol identity;
- the CUDA parity JSON and checksum uploaded by the manually dispatched
  `modern-cuda.yml` self-hosted NVIDIA job;
- Windows 10 22H2 build 19045 evidence at 150% scaling and its checksum;
- Windows 11 build 22000 or newer evidence at 200% scaling with `-RequireCuda`
  and its checksum;
- one completed manual-observation JSON and checksum for each Windows host.

Create a report checksum with:

```powershell
$path = 'C:\validation\evidence\v1-real-data-report.json'
$hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
$name = Split-Path -Leaf $path
Set-Content -LiteralPath "$path.sha256" -Value "$hash  $name" -Encoding ascii
```

Copy `modern/validation/windows-manual-observation.example.json` for each host.
Set the exact host role, observer, UTC time, and automated host-evidence hash.
Set a check to `true` only after personally observing it, then create the same
checksum format. Do not use the synthetic fixtures from
`test-v1-release-evidence.ps1` as release evidence.

## Run

```powershell
.\modern\scripts\verify-v1-release-evidence.ps1 `
  -Archive C:\validation\dslt-workbench-1.0.0-win-x64-cuda.zip `
  -Checksum C:\validation\dslt-workbench-1.0.0-win-x64-cuda.zip.sha256 `
  -RealDataManifest C:\validation\real-data-manifest.json `
  -RealDataReport C:\validation\evidence\v1-real-data-report.json `
  -RealDataReportChecksum C:\validation\evidence\v1-real-data-report.json.sha256 `
  -CudaEvidence C:\validation\evidence\cuda-parity.json `
  -CudaEvidenceChecksum C:\validation\evidence\cuda-parity.json.sha256 `
  -Windows10Evidence C:\validation\evidence\windows-10-150.json `
  -Windows10EvidenceChecksum C:\validation\evidence\windows-10-150.json.sha256 `
  -Windows10Observations C:\validation\evidence\windows-10-150.manual.json `
  -Windows10ObservationsChecksum C:\validation\evidence\windows-10-150.manual.json.sha256 `
  -Windows11Evidence C:\validation\evidence\windows-11-200.json `
  -Windows11EvidenceChecksum C:\validation\evidence\windows-11-200.json.sha256 `
  -Windows11Observations C:\validation\evidence\windows-11-200.manual.json `
  -Windows11ObservationsChecksum C:\validation\evidence\windows-11-200.manual.json.sha256 `
  -OutputPath C:\validation\evidence\v1-release-gate.json
```

The gate rejects checksum or filename mismatches, a package other than the
exact `1.0.0` CUDA candidate, unfinished compatibility rows, mismatched source
commits, weakened real-data thresholds, fewer than five representative
acquisitions, incomplete voxel/channel/Z-spacing coverage, any failed metric,
incomplete CUDA operation coverage, wrong Windows builds or DPI, package hash
mismatches, and any unconfirmed manual observation.

On success, `v1-release-gate.json` has `passed: true` and records the hashes of
all evidence. On failure, the same report format is written with `passed:
false` and the rejection reason; a failed report is never release approval.
The CI fixture script proves both the positive path and rejection of a changed
manual observation without claiming that its synthetic data is real evidence.
