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
- mean/Gaussian smoothing;
- cubic/spherical dilation and erosion;
- area-average and Lanczos 2/3 Z resampling; and
- XY, YZ, and ZX orthogonal-view extraction;
- filtered height maps and normal/Z height projection; and
- 3D depth maps derived from the filtered height surface; and
- 6/18/26-connected component labeling with minimum-size filtering; and
- H-minima reconstruction with exact fixed-point detection; and
- selected-seed watershed with the fixed 256-level legacy schedule; and
- the ordered geodesic DSLT directional-threshold basis; and
- scalar descending threshold sweep with segmentation-state accumulation; and
- iterative DSLT segmentation with one cached directional response and an
  ascending, endpoint-inclusive C schedule.

## Resource ownership and execution

Each request owns a non-blocking CUDA stream plus its input and output device
buffers. RAII destructors release all resources; the backend never calls
`cudaDeviceReset()` and does not use global texture or context state.

Before allocation, the backend checks input and output size arithmetic and
compares their combined size with `cudaMemGetInfo`. This also covers operations
whose output shape differs from the input, such as Z resampling and orthogonal
views. Allocation and runtime failures
are converted into DSLT error states and UTF-8 diagnostic text. Every kernel
launch is checked with `cudaGetLastError`, and the stream is synchronized
before host output is published. Iterative DSLT segmentation includes both
directional-response volumes, morphology workspaces, component-root buffers,
the convergence flag, and optional crop height map in the VRAM preflight.

Progress is reported at start, input transfer, execution, and completion.
Smoothing and morphology synchronize and report after every output Z slice so
longer filters can be cancelled between slices. Cancellation drains the owned
stream and discards the partial result.

Z resampling likewise synchronizes and reports after every output slice. The
CUDA result carries its own output width, height, depth, and kind so the C ABI
publishes the same variable-shape metadata as the CPU reference. Orthogonal
views run as one plane extraction and publish `DSLT_OUTPUT_IMAGE_FLOAT32`.

Height-map operations use the CPU reference's separable clamp-boundary line
weights, threshold-crossing interpolation, and optional repeated XY smoothing.
Normal and Z projections preserve the reference sampling and threshold rules.
Depth maps run a two-pass squared-distance calculation for each output Z slice.
These phases synchronize at cancellable boundaries and include their temporary
volume and plane buffers in the VRAM preflight estimate. Optional RGB depth
coloring requests the same CUDA height surface, depth volume, and scalar Z
projection sequentially; deterministic HSV-to-RGB byte composition occurs in
the WPF presentation layer and does not introduce a separate CUDA result kind.

Connected components propagate the minimum linear voxel index through each
foreground component, then compact accepted roots in ascending index order.
This produces the same deterministic labels as the CPU Z-Y-X seed scan for all
three connectivity modes. Two 64-bit root workspaces are included in VRAM
preflight; final labels and component count cross the CUDA boundary as native
`DSLT_OUTPUT_LABELS_INT32` results rather than being converted through floats.

H-minima initializes the `I + h` marker and repeatedly applies the reference
3 x 3 x 3 minimum followed by lower-mask fitting. Each iteration uses separate
current/next buffers, performs exact change detection, and is cancellable after
synchronization. The volume-derived iteration bound and its overflow use the
same resource-limit status as CPU; the validated check interval remains part of
the request even though consecutive exact equality can finish earlier.

Watershed accepts the engine's current labels, selected seed labels, and crop
state through a request-local state view. It preserves the CPU reference's
ordered six-neighbor flooding, synchronous radius-one label opening after each
of 256 levels, minimum seed-size filtering, fixed or height-map crop, and Int32
labels. Separate current/next label buffers and a convergence flag are included
in VRAM preflight. Progress remains monotonic after input transfer, cancellation
is checked only after synchronized work, and failure never replaces the prior
engine result.

The DSLT threshold basis constructs directions and line weights with the same
host routines as CPU, then visits radii from largest to smallest and directions
in encounter order on one stream. Each voxel keeps a candidate only on a
strictly smaller response, preserving legacy first-minimum tie behavior.
Trilinear sampling clamps every coordinate to the nearest edge, and the final
kernel applies the direction-dependent XY/Z correction with a strict threshold.
Minimum-response, direction-alpha, and maximum line-weight buffers are included
in VRAM preflight; the CPU work estimate and resource gate run before allocation.

Threshold sweep shares the CPU schedule generator, runs binary thresholding,
spherical closing, fixed/height-map crop, wall-opening trials, low-valued
six-connected root propagation, and invalid-structure closing on CUDA. Root
compaction is deterministic by ascending linear root. Component approval and
append-only result-label assignment remain host ordered because their sequence
is observable in the public label contract. Current/next float workspaces, two
64-bit root buffers, convergence state, and optional crop height map are all
included in VRAM preflight.

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

`cuda-runtime-parity` depends on `cuda-build-only`, downloads that job's exact
artifact on a self-hosted runner with the `Windows`, `X64`, and `NVIDIA` labels,
and runs the bundle through `run-cuda-artifact-tests.ps1`. The GPU host therefore
needs a compatible NVIDIA driver but not CMake, MSVC, or a CUDA toolkit. The job
is started only by an explicit `workflow_dispatch`; ordinary pushes run the
hosted build gate without occupying the GPU host. Its native test suite checks:

- CPU/CUDA output parity for every ported operation;
- `Auto` selecting CUDA for a ported operation;
- explicit rejection of an unported operation;
- cancellation propagation; and
- 100 repeated operations without more than 1 MiB apparent free-memory drift
  after kernel warm-up.

The first fully automated hosted-build/self-hosted-runtime chain passed on
2026-08-04 (Asia/Seoul):

| Evidence | Value |
|---|---|
| Source commit | `34f9f6ee3801acba889e06b1595eca91eed7c960` |
| Workflow run | GitHub Actions run `30865538199` |
| Hosted job | `cuda-build-only` passed |
| GPU job | `cuda-runtime-parity` passed |
| Runner | `SNUPCB-HJUNG-RTX4060-2` (`Windows`, `X64`, `NVIDIA`) |
| GPU | NVIDIA GeForce RTX 4060, compute capability 8.9, 8188 MiB |
| Driver | 591.86 |
| Persistence | Current-user scheduled task `DSLT Workbench GitHub Runner` |

The runner is repository-scoped. Its logon task starts the official GitHub
Actions runner v2.336.0 with limited user rights; the CUDA workflow remains
manual-dispatch-only so ordinary pushes cannot occupy the local GPU host.

Pointwise float parity uses an absolute tolerance of `1e-6`, which is stricter
than the project-wide float gate (`abs <= 1e-5` or `rel <= 1e-4`). Binary
threshold results are therefore voxel-exact on the test fixture.

The resampling fixture checks both Lanczos orders and area averaging against
the CPU reference at the project-wide float tolerance. Orthogonal-view values
must be voxel-exact and their output dimensions and image output kind must
match. CPU and CUDA share checked target-spacing-to-depth rounding and reject a
result deeper than uint32 or addressable output limits before allocation.
Invalid spacing, Lanczos order, and slice index plus mid-resample cancellation
are covered.

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

## Recorded resampling and orthogonal-view runtime evidence

The first Z-resampling and orthogonal-view runtime gate was completed on
2026-08-04 (Asia/Seoul):

| Evidence | Value |
|---|---|
| Source commit | `b4440521015c00a52b1f5579b6802525f65cf52b` |
| Hosted build | GitHub Actions run `30851354796`, job `cuda-build-only` |
| Compiler | CUDA 13.2.86 with Visual Studio 2022 |
| Runtime GPU | NVIDIA GeForce RTX 4060, compute capability 8.9, 8188 MiB |
| Driver | 591.86 |
| `dslt_core.dll` SHA-256 | `9E60B1CBE191C1CD8E8A9FF8B9C47ED91309F9BC86C3662E60A3836847850EC7` |
| `dslt_native_tests.exe` SHA-256 | `74477A299C517E771A630F1E92F6F2DAB3CE1F37246E58B124AF7A750B273F05` |
| Result | `DSLT native synthetic tests passed`; `CUDA artifact runtime tests passed` |

The fixture compares area averaging and Lanczos orders 2 and 3 with the CPU
reference on a calibrated 3D volume. It verifies variable output depth, exact
XY/YZ/ZX plane values and dimensions, image output kind, `Auto` selection,
invalid parameters, and cancellation after a completed resampling slice.

The height/depth projection fixture compares Gaussian and mean height surfaces,
normal and Z projections, and volumetric depth values against CPU at the
project float tolerance. It also covers variable image metadata, `Auto`
selection, parameter rejection, and cancellation during depth-slice execution.
After kernel warm-up, the memory fixture cycles pointwise execution, height
maps, depth maps, and both projection modes across 100 requests and permits at
most 1 MiB of apparent free-memory drift.

## Recorded height/depth projection runtime evidence

The first height-map, depth-map, and height-projection runtime gate was
completed on 2026-08-04 (Asia/Seoul):

| Evidence | Value |
|---|---|
| Source commit | `2624b65f81511b9ff9d5a42fc2b0827def43db07` |
| Hosted build | GitHub Actions run `30854398694`, job `cuda-build-only` |
| Compiler | CUDA 13.2.86 with Visual Studio 2022 |
| Runtime GPU | NVIDIA GeForce RTX 4060, compute capability 8.9, 8188 MiB |
| Driver | 591.86 |
| `dslt_core.dll` SHA-256 | `8F865B9C82E56320C6446F72EDECB27694EEFEC96D98BE61BF8DA39114A0DE82` |
| `dslt_native_tests.exe` SHA-256 | `6E968738139C4400EBCC02A748DC2AEA9A953BA1B0273E4712D88FD444EAB9F4` |
| Result | `DSLT native synthetic tests passed`; `CUDA artifact runtime tests passed` |

The runtime fixture exercises mean and Gaussian surface filtering, repeated XY
smoothing, volumetric depth distance, normal and Z projection, variable output
metadata, invalid parameters, `Auto` selection, mid-depth cancellation, and the
100-request mixed-operation memory gate.

The connected-components fixture compares CPU and CUDA labels voxel-for-voxel
for 6, 18, and 26 connectivity with multiple minimum-size limits. It also
covers deterministic component counts, the CPU's NaN-threshold edge behavior,
`Auto` selection, invalid parameters, propagation cancellation, and inclusion
in the mixed-operation memory gate.

## Recorded connected-components runtime evidence

The first connected-components runtime gate was completed on 2026-08-04
(Asia/Seoul):

| Evidence | Value |
|---|---|
| Source commit | `e4f650e99c1b61ef27ea1248049b318acd61437b` |
| Hosted build | GitHub Actions run `30855604638`, job `cuda-build-only` |
| Compiler | CUDA 13.2.86 with Visual Studio 2022 |
| Runtime GPU | NVIDIA GeForce RTX 4060, compute capability 8.9, 8188 MiB |
| Driver | 591.86 |
| `dslt_core.dll` SHA-256 | `6453C16FEE88066267050EE46726001792E7D5881DB06FCD1CF2599FB8E7719D` |
| `dslt_native_tests.exe` SHA-256 | `60259B28104C3907DB91E690FFB652E3844DED6C4B9A666851A99651EDD32DB0` |
| Result | `DSLT native synthetic tests passed`; `CUDA artifact runtime tests passed` |

The runtime fixture proves voxel-exact deterministic labels and component
counts for 6, 18, and 26 connectivity at two minimum-size limits. It also
executes NaN-threshold behavior, `Auto`, invalid arguments, propagation
cancellation, C ABI label copying, and repeated root-workspace allocation.

The H-minima fixture compares deep and shallow 3D pits, zero height, check
intervals 1 and 50, and the final binary mask voxel-for-voxel with CPU. It also
covers `Auto`, invalid height/interval, non-finite source rejection,
reconstruction cancellation, and inclusion in the mixed-operation memory gate.

## Recorded H-minima runtime evidence

The first H-minima runtime gate was completed on 2026-08-04 (Asia/Seoul):

| Evidence | Value |
|---|---|
| Source commit | `b24be0498bfa51010e6fe67de95b8d99df47a918` |
| Hosted build | GitHub Actions run `30857716787`, job `cuda-build-only` |
| Compiler | CUDA 13.2.86 with Visual Studio 2022 |
| Runtime GPU | NVIDIA GeForce RTX 4060, compute capability 8.9, 8188 MiB |
| Driver | 591.86 |
| `dslt_core.dll` SHA-256 | `64AC6EEE4F34B1BFF4C9A5D5614E82DE409ADBF016EA71F61772B2F750F62FB9` |
| `dslt_native_tests.exe` SHA-256 | `2924C4AC497533609491964C86EE093245F13975E2DE8F70C96049BAA9F10A30` |
| Result | `DSLT native synthetic tests passed`; `CUDA artifact runtime tests passed` |

The runtime fixture proves voxel-exact binary masks for deep and shallow 3D
pits, zero height, and check intervals 1 and 50. It also executes `Auto`,
invalid parameter and source rejection, iteration cancellation, and repeated
reconstruction workspace allocation.

## Recorded seeded-watershed runtime evidence

The first selected-seed watershed runtime gate was completed on 2026-08-04
(Asia/Seoul):

| Evidence | Value |
|---|---|
| Source commit | `a5983463ea317ff5ba5944f405fd01672eafbf2c` |
| Hosted build | GitHub Actions run `30859907320`, job `cuda-build-only` |
| Compiler | CUDA 13.2.86 with Visual Studio 2022 |
| Runtime GPU | NVIDIA GeForce RTX 4060, compute capability 8.9, 8188 MiB |
| Driver | 591.86 |
| `dslt_core.dll` SHA-256 | `02620DD4391F376BAD3F0199DD4761B7DFD1DCACA300E526D81559CC04E7400E` |
| `dslt_native_tests.exe` SHA-256 | `3A3D9E5E6D045D8130013C5285B9DECF736901C8C26A1781BCE530D97061EC63` |
| Result | `DSLT native synthetic tests passed`; `CUDA artifact runtime tests passed` |

The runtime fixture proves voxel-exact CPU/CUDA labels and component counts for
two competing seeds. It also covers selected-seed restriction, minimum seed
size, fixed crop, all 256 levels, `Auto`, invalid state and parameters,
mid-level cancellation, and repeated watershed workspace allocation in the
mixed-operation memory gate.

## Recorded DSLT threshold-basis runtime evidence

The first directional DSLT threshold runtime gate was completed on 2026-08-04
(Asia/Seoul):

| Evidence | Value |
|---|---|
| Source commit | `255b8441982212ff85896204a9b62b7e5cfc2d8d` |
| Hosted build | GitHub Actions run `30860933349`, job `cuda-build-only` |
| Compiler | CUDA 13.2.86 with Visual Studio 2022 |
| Runtime GPU | NVIDIA GeForce RTX 4060, compute capability 8.9, 8188 MiB |
| Driver | 591.86 |
| `dslt_core.dll` SHA-256 | `53325895878F0B9A0CD96F92E67F31510DC750B615B3AEC940CAD330A11636D1` |
| `dslt_native_tests.exe` SHA-256 | `A4B98ECD57EE19C8E636F7C18DA1B737C62C39924ADC80F121139AC72D1B9AC2` |
| Result | `DSLT native synthetic tests passed`; `CUDA artifact runtime tests passed` |

The runtime fixture proves voxel-exact final masks for mean and Gaussian line
weights, radii 1 and 2, direction levels 1 and 2, oblique directions, and three
XY/Z correction combinations. It also covers `Auto`, parameter bounds,
directional-work rejection, mid-direction cancellation, and repeated response
workspace allocation. The same response kernels are reused by the iterative
DSLT segmentation port recorded below.

## Recorded threshold-sweep runtime evidence

The first scalar threshold-sweep runtime gate was completed on 2026-08-04
(Asia/Seoul):

| Evidence | Value |
|---|---|
| Source commit | `1a0ff05ac4c57308d08d15948c37717549229b12` |
| Hosted build | GitHub Actions run `30862128131`, job `cuda-build-only` |
| Compiler | CUDA 13.2.86 with Visual Studio 2022 |
| Runtime GPU | NVIDIA GeForce RTX 4060, compute capability 8.9, 8188 MiB |
| Driver | 591.86 |
| `dslt_core.dll` SHA-256 | `168A9599B41963FDB84515B7265F257905BEEFD99F9644DD40710C4C5ED7A190` |
| `dslt_native_tests.exe` SHA-256 | `C60D37CA0856753984FF33AC514AB5299F7CD32751A6FC7B88916627FF49E456` |
| Result | `DSLT native synthetic tests passed`; `CUDA artifact runtime tests passed` |

The runtime fixture proves voxel-exact labels, component count, and completed
passes for the two-pass staged-defect oracle. It also covers radius-one closing,
fixed and height-map crop, `Auto`, schedule and parameter rejection, progress
cancellation, exclusive minimum component size, final-pass acceptance, and
repeated sweep workspace allocation. The same deterministic component and wall
validation stages are reused by iterative DSLT segmentation.

## Recorded iterative DSLT segmentation runtime evidence

The first full iterative DSLT segmentation runtime gate was completed on
2026-08-04 (Asia/Seoul):

| Evidence | Value |
|---|---|
| Source commit | `2f62079ebf45252c7ba48f4488e1fb4cccd910ab` |
| Hosted build | GitHub Actions run `30863620628`, job `cuda-build-only` |
| Compiler | CUDA 13.2.86 with Visual Studio 2022 |
| Runtime GPU | NVIDIA GeForce RTX 4060, compute capability 8.9, 8188 MiB |
| Driver | 591.86 |
| `dslt_core.dll` SHA-256 | `80D03A1A839A88D1912FBDF93BF3C36921DAB6BF71CBCA2AA788D14E0282698F` |
| `dslt_native_tests.exe` SHA-256 | `90144F7B5ADB8D700A4EAC44052DAC4304FF2F0EF55BF6F9AEA05FA8F8C7EFCE` |
| Result | `DSLT native synthetic tests passed`; `CUDA artifact runtime tests passed` |

The runtime fixture proves voxel-exact CPU/CUDA labels, component count, and
completed-pass count for both a general volume and a staged two-pass defect
volume. It also covers radius-one closing, height-map crop, `Auto`, invalid C
schedule and limit rejection, mid-response cancellation, and repeated combined
response/sweep workspace allocation without measured GPU-memory growth.
