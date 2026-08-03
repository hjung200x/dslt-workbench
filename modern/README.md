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

The CPU backend is the reference implementation. CUDA is an optional backend;
when it is unavailable, `Auto` always selects CPU and reports the fallback.

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
optional and never changes CPU semantics.

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
non-x64 PE files, an incomplete GPLv3 copy, missing upstream provenance, unsafe
or duplicate ZIP paths, missing legacy/.NET notices, or a checksum mismatch.
See `docs/release-policy.md`.

## Real-data release validation

Use `tools/Dslt.Validation` with
`validation/real-data-manifest.example.json` to evaluate five or more accepted
legacy/expert label pairs. The tool verifies input, label, and provenance hashes
before calculating Dice, object-count, volume, and HD95 gates. See
`docs/real-data-validation.md`.

## Validation status

Only synthetic-data equivalence can be claimed until representative confocal
stacks and accepted reference results are available. See
`docs/validation-policy.md`. Legacy-to-Workbench parameters and error rules are
tracked in `docs/functional-spec.md` and `docs/compatibility-matrix.md`. The
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

The current WPF workflow, state-preservation guarantees, and the remaining
orthogonal-view and label-editing UI work are tracked in
`docs/ui-workflow.md`.
