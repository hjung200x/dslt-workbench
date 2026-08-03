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

## Validation status

Only synthetic-data equivalence can be claimed until representative confocal
stacks and accepted reference results are available. See
`docs/validation-policy.md`. Legacy-to-Workbench parameters and error rules are
tracked in `docs/functional-spec.md` and `docs/compatibility-matrix.md`.
