# CUDA backend contract

The CPU implementation is the behavioral reference. CUDA operations must use
the same selected channel, parameter interpretation, boundary rules, output
shape, and output kind as CPU.

## Selection and failure policy

- `CPU` always runs the CPU reference implementation.
- `CUDA` fails with `DSLT_BACKEND_UNAVAILABLE` when no usable device exists and
  `DSLT_NOT_IMPLEMENTED` when the requested operation has not been ported.
- `Auto` uses CUDA only for a ported operation on an available device. It uses
  CPU when CUDA initialization is unavailable or the operation is not ported.
- Invalid parameters, cancellation, insufficient memory, and kernel/runtime
  failures are returned to the caller. They do not silently rerun on CPU.

The currently ported groups are:

- `Copy`, `WindowLevel`, `Threshold2D`, and `Threshold3D`;
- mean/Gaussian smoothing; and
- cubic/spherical dilation and erosion.

## Resource ownership and execution

Each request owns a non-blocking CUDA stream plus its input and output device
buffers. RAII destructors release all resources; the backend never calls
`cudaDeviceReset()` and does not use global texture or context state.

Before allocation, the backend checks size arithmetic and compares the two
required float buffers with `cudaMemGetInfo`. Allocation and runtime failures
are converted into DSLT error states and UTF-8 diagnostic text. Every kernel
launch is checked with `cudaGetLastError`, and the stream is synchronized
before host output is published.

Progress is reported at start, input transfer, execution, and completion.
Smoothing and morphology synchronize and report after every output Z slice so
longer filters can be cancelled between slices. Cancellation drains the owned
stream and discards the partial result.

## Validation gates

`cuda-build-only` runs on a GitHub-hosted Windows Server 2022 image. It installs
the minimal official CUDA 13.2 compiler/runtime packages with NVIDIA's silent
network installer, then compiles the CUDA DLL and CUDA-enabled native tests.
This gate proves NVCC/MSVC/CMake compatibility but cannot execute kernels
because the hosted runner has no NVIDIA device. A successful job uploads the
DLL and native test executable as `dslt-cuda-tests-win-x64-<commit>` for seven
days. The bundle can be downloaded to a trusted Windows NVIDIA host and run
without installing a compiler:

```powershell
gh run download <run-id> --name dslt-cuda-tests-win-x64-<commit> --dir .run/cuda-tests
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  ./modern/scripts/run-cuda-artifact-tests.ps1 -ArtifactDirectory .run/cuda-tests
```

Only bundles produced from the repository's own reviewed commit should be
executed. The script requires `nvidia-smi`, verifies the expected DLL/executable
layout, reports the selected GPU, and propagates any native test failure.

`cuda-runtime-parity` uses the `windows-cuda` preset on a self-hosted runner
with the `Windows`, `X64`, and `NVIDIA` labels. It defines `DSLT_TEST_CUDA` and
requires a usable NVIDIA device. Until such a runner is registered, this job is
started only by an explicit `workflow_dispatch`; ordinary pushes run the hosted
build gate without leaving an unserviceable job queued. Its native test suite
checks:

- CPU/CUDA output parity for every ported operation;
- `Auto` selecting CUDA for a ported operation;
- explicit rejection of an unported operation;
- cancellation propagation; and
- 100 repeated operations without more than 1 MiB apparent free-memory drift
  after kernel warm-up.

Pointwise float parity uses an absolute tolerance of `1e-6`, which is stricter
than the project-wide float gate (`abs <= 1e-5` or `rel <= 1e-4`). Binary
threshold results are therefore voxel-exact on the test fixture.

## Recorded pointwise runtime evidence

The first pointwise runtime gate was completed on 2026-08-04 (Asia/Seoul):

| Evidence | Value |
|---|---|
| Source commit | `5235fbb749ed65b7f89135e731d3e9761dcf7a59` |
| Hosted build | GitHub Actions run `30847841411`, job `cuda-build-only` |
| Compiler | CUDA 13.2.86 with Visual Studio 2022 |
| Runtime GPU | NVIDIA GeForce RTX 4060, compute capability 8.9, 8188 MiB |
| Driver | 591.86 |
| `dslt_core.dll` SHA-256 | `8CE96CB790BC61984D8C306ACCCCF73CFC4822FB26790B432A4D431C7B8A9250` |
| `dslt_native_tests.exe` SHA-256 | `6940375001A44C5E107B1B3FDF9C2194785E2FE462019842F54213FAB84CDF83` |
| Result | `DSLT native synthetic tests passed`; `CUDA artifact runtime tests passed` |

This proves the ported pointwise group on one Ada GPU and driver combination.
It does not replace the required coverage of the remaining CUDA operations or
future repeated validation on the registered CI GPU fleet.

## Recorded filtering runtime evidence

The first smoothing and morphology runtime gate was completed on 2026-08-04
(Asia/Seoul):

| Evidence | Value |
|---|---|
| Source commit | `f071bcd29ecf341ef831f78cdab1d6fcda99e9c8` |
| Hosted build | GitHub Actions run `30849982699`, job `cuda-build-only` |
| Compiler | CUDA 13.2.86 with Visual Studio 2022 |
| Runtime GPU | NVIDIA GeForce RTX 4060, compute capability 8.9, 8188 MiB |
| Driver | 591.86 |
| `dslt_core.dll` SHA-256 | `CE6DD7D8FC55C313C3287681DFB709A56730D080700953100C6B9001E38D993D` |
| `dslt_native_tests.exe` SHA-256 | `9BE390AAE18286B7ED68F7CA2B60F5969390D18F9CF4087FFB51EF181466DBAB` |
| Result | `DSLT native synthetic tests passed`; `CUDA artifact runtime tests passed` |

The fixture compares mean/Gaussian smoothing and cubic/spherical dilation and
erosion against CPU on a nontrivial 3D volume. It also covers clamp boundaries,
zero radius, invalid radius, `Auto` selection, and cancellation after a
completed output Z slice. Smoothing uses the project float tolerance; morphology
is voxel-exact on the fixture.
