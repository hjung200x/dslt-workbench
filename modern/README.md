# DSLT Workbench

DSLT Workbench is a GPL-3.0-or-later workspace for reconstructing the legacy
DSLT Demo while preserving its processing behavior. The original Visual Studio
2012 sources remain unchanged at the repository root and are pinned by the
`legacy-baseline-aae2b3e` tag.

## Architecture

- `native/`: C++20 processing core and versioned C ABI (`dslt_core_v1`)
- `app/`: .NET 10 WPF application using MVVM and P/Invoke
- `tests/`: synthetic data and cross-layer smoke tests
- `docs/`: compatibility matrix, validation policy, and provenance
- `legacy/`: hash-locked original GPLv3 manual and asset provenance

The CPU backend is the reference implementation. CUDA is an optional backend.
`Auto` selects CUDA for operations that have passed CPU parity validation and
falls back to CPU when CUDA is unavailable or the operation is not yet ported.
CUDA execution failures are reported instead of being silently retried on CPU.

## Build

### Managed application

```powershell
dotnet build modern/app/Dslt.App/Dslt.App.csproj
dotnet run --project modern/tests/Dslt.Managed.Tests/Dslt.Managed.Tests.csproj
dotnet run --project modern/tests/Dslt.App.Tests/Dslt.App.Tests.csproj
```

### Native core

Visual Studio 2022 with Desktop C++ and CMake support is required.

```powershell
cmake --preset windows-cpu
cmake --build --preset windows-cpu-release
ctest --preset windows-cpu
```

Use the `windows-cuda` preset after installing CUDA 13.2. The CUDA target is
optional and never changes CPU semantics. The first validated CUDA group is
copy, window/level, and global 2D/3D threshold; see `docs/cuda-backend.md`.

## Preview package

Build, assemble, and verify a self-contained CPU preview package from a Visual
Studio developer shell:

```powershell
.\modern\scripts\package-preview.ps1 -Version 0.1.0-preview
```

Packaging requires a clean Git worktree so `BUILD-INFO.json` identifies the
exact distributed source commit.

The command creates a Windows x64 ZIP and adjacent SHA-256 file under
`modern/artifacts/preview`. It rejects missing native/runtime binaries,
non-x64 PE files, an incomplete GPLv3 copy, missing upstream provenance, a
changed legacy manual, unsafe or duplicate ZIP paths, missing legacy/.NET
notices, or a checksum mismatch.
See `docs/release-policy.md`.

## Real-data release validation

Use `tools/Dslt.Validation` with
`validation/real-data-manifest.example.json` to evaluate five or more accepted
legacy/expert label pairs. The tool verifies input, label, and provenance hashes
before calculating Dice, object-count, volume, and HD95 gates. See
`docs/real-data-validation.md`. Candidate sources, licenses, and the boundary
between public pipeline preflight and representative release evidence are
tracked in `docs/real-data-cohort-sources.md`.
`tools/Dslt.Validation.Prepare` normalizes external reference masks and builds
source-locked schema-2 manifest cases directly from candidate provenance.
The schema-2 manifest and provenance 1.10 sidecars must identify the exact
release-candidate source commit; results from another build are rejected.


## V1.0 release evidence

After all compatibility-matrix rows are complete, build the exact CUDA release
candidate with `package-preview.ps1 -Version 1.0.0 -Cuda -ReleaseCandidate`.
`verify-v1-release-evidence.ps1` then binds that package to the real-data,
CUDA, Windows 10/11, and manual-observation evidence and emits the final
SHA-256-locked decision. See `docs/v1-release-evidence.md`.

## Validation status

Only synthetic-data equivalence can be claimed until representative confocal
stacks and accepted reference results are available. See
`docs/validation-policy.md`. Legacy-to-Workbench parameters and error rules are
tracked in `docs/functional-spec.md` and `docs/compatibility-matrix.md`. The
preserved version 1.11 manual and the explicit interaction gaps found during its
10-page audit are recorded in `docs/legacy-manual-audit.md`. The
directional threshold equations, sampling rules, parameter mapping, and
required fixtures are fixed in `docs/dslt-algorithm-spec.md`. Decoded image
types, ImageJ ordering, calibration, LSM limitations, and signed label export
are defined in `docs/tiff-io-spec.md`.

The source-derived mean/Gaussian local-threshold equations, clamp boundary,
2D/3D distinction, legacy C scaling, and known GPU discrepancy are defined in
`docs/adaptive-threshold-spec.md`.

The erosion-reconstruction marker, mask fitting, convergence, residual
threshold, and hidden check-interval contract for H-minima are defined in
`docs/h-minima-spec.md`.

The selected-label seed contract, fixed flood schedule, legacy neighbor
priority, per-level label opening, crop behavior, and provenance rules for
Watershed are defined in `docs/watershed-spec.md`.

The source-derived height-map Z filter, crossing interpolation, XY smoothing,
defaults, and legacy CPU/GPU path distinction are defined in
`docs/height-map-spec.md`.

The legacy A-command height-surface area workflow uses the active `.hmp` or
generated surface with the fixed 10 x 10 Simpson rule. It provides a normalized
Gray8 preview, a quantitative IEEE Float32 TIFF, cancellation, and surface-hash
provenance; see `docs/height-surface-area-spec.md`.

The exact 3D Euclidean depth map, scalar Z/normal projection, range and offset
semantics, threshold behavior, and Z-only RGB depth-color presentation are
defined in `docs/height-projection-spec.md`. Depth and projection operations
reuse the compatible active `.hmp`/generated surface; when none is active they
generate one from the visible height-map parameters.

The WPF operation selector exposes 2D/3D thresholding, cube/sphere morphology,
depth-dependent Z-gradient correction with an optional filtered height surface,
and calibrated Z area-average or Lanczos 2/3 resampling. Z resampling defaults
to the input X spacing and rejects unaddressable output depth before allocation.

The current WPF workflow, state-preservation guarantees, responsive layout, and
remaining interactive checks are tracked in `docs/ui-workflow.md`. Repeatable
Windows 10/11 package evidence is collected according to
`docs/windows-host-validation.md`.
