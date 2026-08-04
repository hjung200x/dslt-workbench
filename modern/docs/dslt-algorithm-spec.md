# DSLT algorithm compatibility specification

## Status and authority

This document is the implementation contract for Direction-Selective Local
Thresholding (DSLT) in Workbench. It records the behavior of the pinned legacy
source at `legacy-baseline-aae2b3e` and maps it to the method described in
*A direction-selective local-thresholding method, DSLT, in combination with a
dye-based method for automated three-dimensional segmentation of cells and
airspaces in developing leaves*
([DOI 10.1111/tpj.12738](https://doi.org/10.1111/tpj.12738)).

The paper establishes algorithmic intent. The active pinned source establishes
the compatibility behavior where it is unambiguous. Any behavior marked
`capture-required` must be measured with the legacy executable before
Workbench may claim legacy equivalence.

Implementation status: **scalar CPU threshold basis, iterative C sweep, crop
integration, CUDA threshold basis, and CUDA scalar threshold sweep implemented;
legacy-runtime comparison pending**. The operation covers
ordered directions, every-radius response, trilinear clamp sampling,
direction-dependent C, and strict binarization. The response and C application
are separate internal operations so one response is reused throughout the C
sweep. Buffer-based spherical closing, low-valued 6-connected component
extraction, wall-thickness estimation, invalid-structure rejection,
append-only labeling, final-pass acceptance, early termination, fixed-Z crop,
height-map-relative crop, and XY border exclusion are wired to
`DSLT_OP_DSLT_SEGMENTATION`.

## Terminology and coordinates

- A voxel location is `p = (x, y, z)` in an `X x Y x Z` float volume `I`.
- `R` is the line-kernel radius. Every odd length `2r + 1`, for
  `r = R, R-1, ..., 1`, participates in the minimum search.
- `D_l` is the ordered direction set produced at subdivision level `l`.
- `k` is an integer sample offset in `[-r, r]`.
- `S(I, q)` is trilinear sampling at the non-integral position `q`, with each
  coordinate clamped to the nearest volume edge.
- Legacy binary lower and upper values are respectively `0.0` and `0.8`.

The source stores voxels with X as the contiguous dimension, followed by Y and
Z. CUDA samples unnormalized texture coordinates at voxel centers by adding
`0.5` on all axes (`linearConvolution_Texture.cu:93-101, 105-130`).

## Ordered direction construction

The active implementation does not use the older angular-loop code. It starts
with the 12 vertices and 20 faces of an icosahedron, subdivides every triangular
face `l` times, removes the second member of each antipodal pair in encounter
order, and normalizes the remaining vertices (`filter3d.cpp:1998-2083`).

For valid `l >= 1`, the expected number of unoriented line directions is:

```text
|D_l| = 5 * 4^l + 1
```

The direction vector is treated as an unoriented line: `d` and `-d` are not
both evaluated. Ordering is observable because exact convolution ties retain
the first radius/direction encountered.

The legacy parameter named `angle_d` is therefore a **geodesic subdivision
level**, not an angular increment. Workbench names it `directionLevel` in new
interfaces while accepting the legacy alias during migration.

## Line kernels and directional response

For mean weighting:

```text
w_r(k) = 1 / (2r + 1)
```

For Gaussian weighting:

```text
sigma_r = 0.3 * (r - 1) + 0.8
g_r(k)  = exp(-(k^2) / (2 * sigma_r^2))
w_r(k)  = g_r(k) / sum_j(g_r(j))
```

These definitions match `filter3d.cpp:4786-4812`. The directional response is:

```text
L(r, d, p) = sum[k=-r..r] w_r(k) * S(I, p + k*d)
```

The minimum response and its direction are:

```text
(Lmin(p), dmin(p)) = first arg min over
                     r = R..1, then d in D_l encounter order
```

The active loops are at `filter3d.cpp:2139-2164`; the CUDA kernel updates only
when `previous > candidate`, not when equal
(`linearConvolution_Texture.cu:118-130`). Consequently:

- a tie between radii keeps the larger radius because radii are visited from
  large to small;
- a tie within one radius keeps the earlier direction;
- CPU code must use the same ordering and a strict comparison;
- optimized reductions may not reorder candidates unless tests prove that the
  resulting threshold and mask remain identical.

CUDA texture configuration confirms trilinear interpolation and clamp-to-edge
addressing (`linearConvolution_Texture.cu:73-101`). CPU and Workbench CUDA code
must reproduce those semantics explicitly rather than depend on library
defaults.

## Direction-dependent C correction

For a normalized direction `d = (dx, dy, dz)`, the legacy code records:

```text
latitude = acos(sqrt(dx^2 + dy^2))
alpha    = abs(2 * latitude / pi)
```

Thus `alpha = 0` for an XY-plane line and `alpha = 1` for a Z-axis line. Given
core parameters `Cxy` and `Cz`, the blended correction is:

```text
Cdir(p) = Cxy * (1 - alpha(p)) + Cz * alpha(p)
T(p)    = Lmin(p) - Cdir(p)
B(p)    = 0.8 if I(p) > T(p), otherwise 0.0
```

The correction kernel writes `-Cdir`, adds it to `Lmin`, and then binarizes
with zero constant (`linearConvolution_Texture.cu:630-667`,
`filter3d.cpp:2510-2525`). The comparison is strictly `>`; equality produces
the lower value (`convolutionSeparable_gold.cpp:124-135`).

The CLR bridge sets `Cz = factorCz * Cxy` and multiplies UI-facing C by
`0.002` (`3DFilter_CLR_Interface.h:16`,
`3DFilter_CLR_Interface.cpp:616-620`). The preview UI additionally negates its
positive C value (`MainWindow.xaml.cs:1042-1046`). Therefore a preview value
`U` maps to:

```text
Cxy = -0.002 * U
Cz  = factorCz * Cxy
T   = Lmin + 0.002 * U * ((1-alpha) + factorCz*alpha)
```

Workbench exposes a positive `thresholdOffset` matching `U` and performs this
mapping in one documented adapter. The native operation contract uses the
unambiguous `Cxy` and `Cz` values.

## Parameter contract

| Parameter | Legacy source behavior | Legacy UI declaration | Workbench contract |
|---|---|---|---|
| `radius` | Rejects `<= 0`; active CUDA dispatcher supports odd lengths through 255, so the effective maximum is 127 | 0..100, default 14 | Integer 1..127; UI default 14 and UI maximum 100 |
| `directionLevel` | Rejects `< 1`; builds `5*4^level+1` directions | 0..10, default 2 | Integer >= 1; checked direction-count/work estimate before allocation; default 2 |
| `kernelType` | 0 Gaussian, 1 mean | Version 1.11 manual preset is Gaussian; repository XAML selects mean | Closed enum `Gaussian` or `Mean`; UI uses the released manual preset `Gaussian` |
| `zCorrectionFactor` | Multiplies `Cxy` to obtain `Cz` | 0..1, default 0.2 | Finite float >= 0; default 0.2 |
| preview `C` | Negated, then scaled by 0.002 | 0..200, default 20 | Finite positive UI offset; default 20; adapter maps to core C values |
| sweep C bounds | CLR negates the visible bounds and scales them and the positive interval by 0.002; core requires `minC <= maxC` | Version 1.11 manual preset: visible max C 10, min C 4, interval -1 | Core preset is `-0.020` through `-0.008` with interval `0.002`; installed-runtime behavior remains `capture-required` |
| `closing` | Spherical maximum then minimum filter | 0..50, default 2 | Integer >= 0; default 2 |
| `validArea` | Converted to volume by multiplying estimated wall thickness | Version 1.11 manual preset 800; repository XAML 500 | Integer >= 0; UI uses the released manual preset 800; exact runtime validation remains `capture-required` |

Levels can become computationally explosive. `dslt_estimate_operation` and the
.NET `EstimateAsync` adapter calculate voxel count, direction count,
`R*(R+2)` line samples per voxel/direction, total directional sample work,
conservative host memory, and C-sweep pass count with checked 64-bit
arithmetic. Execution performs the same preflight and never silently lowers a
parameter. The initial CPU safety gates are 10,000,000,000,000 directional
sample operations and 16 GiB estimated host memory. A rejected request returns
`DSLT_RESOURCE_LIMIT` and an error containing the measured and allowed work,
memory, direction, sample, and pass values. These are safety gates rather than
performance promises; future tiled execution may raise them without changing
algorithm parameters.

The legacy segmentation XAML declares values that conflict with slider ranges
and swaps visible min/max labels (`MainWindow.xaml:694-728`). Workbench therefore
uses the values visible in the preserved version 1.11 manual as its default
preset and documents the exact core conversion above. Archived executable
capture is still required to settle whether the installed binary coerces the
malformed source declarations differently.

## Iterative segmentation workflow

`segmentation_SobelLikeADTH` is the legacy UI action named `SGL`. Despite that
name, its active threshold basis is the geodesic DSLT response, calculated once
for the entire C sweep (`filter3d.cpp:3882-3917`). The compatibility workflow is:

1. Validate nonempty input, positive radius/level/interval, ordered C bounds,
   and nonnegative closing radius.
2. Compute `Lmin` and the argmin latitude once.
3. Apply the current direction-dependent C correction and strict binarization.
4. Perform spherical closing: maximum filter followed by minimum filter.
5. Apply crop rules, estimate wall thickness, and then invert crop-excluded
   regions for low-valued component extraction.
6. Extract low-valued (`<= 0.1`) components with 6-connectivity. A component is
   retained only when `size > min_size`, not `>=`.
7. On later C values, mask already segmented regions to the lower value, repeat
   correction/binarization/closing/crop/validation, and append newly valid
   segments.
8. Advance `C = minC + interval * iteration`, clamp the final iteration to
   `maxC`, and stop early when no invalid segments remain.
9. On the final C pass, disable invalid-structure rejection by passing
   `INT_MAX` as its threshold.

The initial pass is at `filter3d.cpp:3904-3976`, and the repeated pass and stop
conditions are at `filter3d.cpp:3978-4048`. Low-valued component extraction and
the strict minimum-size test are at `filter3d.cpp:2818-2865` and
`filter3d.cpp:2875-2933`.

CPU and CUDA obtain the C values from the same endpoint-inclusive schedule.
CUDA computes the directional minimum and latitude volumes once, keeps them in
dedicated device buffers, and then reuses the scalar-sweep morphology,
wall-thickness, six-connected labeling, invalid-structure, crop, and
append-only label stages. This preserves CPU component order and completed-pass
count while avoiding a host fallback inside a CUDA request.

Watershed is **not** called by this function. It is a separate selected-segment
editing action (`filter3d.cpp:6877-6925`, `MainWindow.xaml.cs:1591-1595`). The
headless validation pipeline can compose it explicitly after DSLT and records
the DSLT label result as the hashed marker-seed step; the native DSLT operation
itself remains unchanged.

## Non-DSLT threshold sweep workflow

`segmentation_Threshold` is a separate legacy operation and does not calculate
the directional DSLT response (`filter3d.cpp:4545-4695`). Workbench exposes it
as `DSLT_OP_THRESHOLD_SWEEP` with the following source-derived ordering:

1. Start at `maximumThreshold`, then subtract the positive interval, clamping
   the last pass to `minimumThreshold` so both bounds are evaluated exactly.
2. Binarize each voxel to `0.8` when `I >= threshold` and `0.0` otherwise.
3. Apply spherical closing, mask already accepted labels to `0.0`, apply crop,
   estimate wall thickness, and invert crop-excluded voxels to `0.8`.
4. Extract low-valued 6-connected components using the exclusive minimum-size
   rule and append accepted labels without replacing earlier labels.
5. Defer components whose closed invalid-structure volume exceeds
   `minimumInvalidStructureArea * wallThickness`; accept every remaining
   component on the final pass.
6. Stop early when a pass leaves no deferred component. Report the exact number
   of completed passes and honor cancellation without publishing partial output.

The legacy XAML declares maximum threshold `1` but an initial value of `20`;
WPF coerces that slider value to `1`. Workbench uses explicit defaults min `0`,
max `1`, interval `0.02`, valid area `100`, and closing radius `2`. Native,
C ABI, managed, and WPF fixtures cover pass order, append-only labels, a staged
defect, final-pass behavior, invalid bounds, cancellation, and parameter
mapping. Archived-runtime comparison remains required before claiming legacy
equivalence.

## Known legacy defects and compatibility decisions

| Observation | Decision |
|---|---|
| Original processing is GPU-only at the CLR boundary | Implement CPU first as the normative Workbench backend, then compare CUDA against it |
| Radius values above 127 pass top-level validation but have no CUDA template dispatch | Reject explicitly with `invalid_argument`; never return an FLT_MAX-derived mask |
| Temporary arrays are sometimes released with scalar `delete` | Fix with RAII; memory-management defects are not compatibility behavior |
| `min_segVol` is accepted by the CLR segmentation method but is not forwarded to the active overload | Preserve the visible parameter in provenance, but do not claim it affected legacy output; define corrected Workbench behavior only after a fixture-backed decision |
| `angle_d` actually means subdivision level | Rename to `directionLevel`, retain legacy alias in import/provenance |
| Sweep XAML ranges, values, labels, and code-behind signs conflict | Do not guess effective defaults; capture the installed legacy UI/runtime |
| Paper workflow includes steps not invoked by the SGL handler | Expose each operation separately and record pipeline composition in provenance |

## Required fixtures and acceptance tests

Stage 7 may mark DSLT `implemented` only after all of the following CPU tests
pass. CUDA must pass the project-wide numerical tolerances against these CPU
results. The CUDA response/mask and scalar threshold-sweep gates are complete;
the combined iterative DSLT segmentation gate remains outstanding.

| Fixture | Required assertion |
|---|---|
| Constant volume | Response equals the constant; radius/direction tie keeps the first candidate; equality threshold yields 0.0 |
| X, Y, and Z ramps | Axis direction and latitude correction are distinguishable and deterministic |
| Oblique planar ramp | Trilinear samples match an independent scalar oracle |
| Edge impulse and edge ramp | Clamp-to-edge behavior matches the scalar oracle at every face, edge, and corner |
| Mean and Gaussian impulse | Weights sum to one and match the formulas for radii 1, 2, and 14 |
| Direction levels 1, 2, and 3 | Direction counts are 21, 81, and 321, with no antipodal duplicate |
| Equal-response directions | Strict first-min tie behavior is stable across repeated runs |
| C sign fixture | UI C=20 and factor=0.2 map to Cxy=-0.04 and Cz=-0.008 |
| Threshold boundary | `I == T` is lower; the next representable value above T is upper |
| Sphere/shell and touching cells | Closing, 6-connectivity, component count, bounding boxes, and volume match expected values |
| C sweep with staged defects | Existing labels are not overwritten; C order, early stop, and final-pass behavior are exact |
| Cancellation/work limit | No partial result replaces the last valid result and the estimate is reported |

The native `dslt_algorithm_tests` target currently fixes the 21/81/321
direction counts and antipodal uniqueness, mean/Gaussian formulae for radii
1/2/14, oblique and clamp-to-edge interpolation, constant-response tie order,
strict threshold boundary, spherical closing, and the legacy-exclusive
low-component size rule. It also uses a staged defect fixture to verify
append-only labels, invalid-component deferral, final-pass acceptance, exact C
pass count, and cancellation. C ABI and managed fixtures verify height-map crop
and XY border behavior. X/Y/Z and oblique ramps are compared voxel-by-voxel
against an independent scalar orchestration for mean and Gaussian kernels.
Checked work/memory estimates and oversized rejection also have native and ABI
fixtures. Archived-binary comparisons are still outstanding.

Until the same fixtures can be run through an archived legacy binary, the
result level is **synthetic-data validated**, not legacy compared or
functionally equivalent.

## ABI v1 operation mapping

The stable v1 C request remains 48 bytes. For `DSLT_OP_DSLT_THRESHOLD`, its
operation-specific fields are interpreted as follows:

| C ABI field | DSLT meaning |
|---|---|
| `radius` | Maximum line radius |
| `lanczos_order` | Geodesic direction level |
| `connectivity` | Kernel type: 0 Gaussian, 1 mean |
| `constant_c` | Core `Cxy` value; no UI sign/scale conversion |
| `target_spacing_z` | Z correction factor used to derive `Cz` |

The .NET `OperationParameters` adapter exposes these as `Radius`,
`DirectionLevel`, `DsltKernel`, `ConstantC`, and `ZCorrectionFactor`; callers do
not need to know the C-field reuse. The WPF ViewModel exposes the positive
legacy preview offset and maps it to `ConstantC = -offset * 0.002` before calling
the managed adapter.

For `DSLT_OP_DSLT_SEGMENTATION`, the same ABI-stable request is interpreted as:

| C ABI field | Iterative DSLT meaning |
|---|---|
| `radius` | Maximum line radius |
| `lanczos_order` | Geodesic direction level |
| `connectivity` | Kernel type: 0 Gaussian, 1 mean |
| `minimum_component_size` | Exclusive minimum low-component size |
| `slice_index` | Spherical closing radius |
| `threshold` | Minimum invalid-structure area; must be a non-negative integer |
| `constant_c` | Minimum core `Cxy` |
| `window_min` | Maximum core `Cxy` |
| `window_max` | Positive C interval |
| `target_spacing_z` | Z correction factor |

The native result's reserved word records completed sweep passes and is exposed
as `.NET ProcessingResult.CompletedPasses`. This adds no fields and preserves
the ABI v1 structure sizes. The .NET adapter exposes semantic properties
`MinimumC`, `MaximumC`, `CInterval`, `ClosingRadius`, and
`MinimumInvalidStructureArea` so application code does not depend on field
reuse.

For `DSLT_OP_THRESHOLD_SWEEP`, the 48-byte v1 request is interpreted as:

| C ABI field | Threshold sweep meaning |
|---|---|
| `minimum_component_size` | Exclusive minimum low-component size |
| `slice_index` | Spherical closing radius |
| `threshold` | Minimum invalid-structure area; must be a non-negative integer |
| `constant_c` | Minimum image threshold |
| `window_min` | Maximum image threshold |
| `window_max` | Positive threshold interval |

The .NET adapter exposes semantic `MinimumThreshold`, `MaximumThreshold`,
`ThresholdInterval`, `ThresholdSweepMinimumComponentSize`, and
`ThresholdSweepMinimumInvalidStructureArea` properties. Separating the last
two prevents the threshold defaults (0 and 100) from being confused with DSLT
segmentation defaults. Completed passes use the same result field as the DSLT
sweep, preserving all ABI v1 structure sizes.

Crop state is configured with the additive ABI v1 function `dslt_set_crop`.
The 16-byte `dslt_crop_options` contains enabled/use-height-map flags, inclusive
upper/lower Z offsets, and XY border thickness. When height-map mode is active,
the caller supplies exactly `width * height` finite float samples. As in the
legacy loop, excluded voxels are first forced to the lower value for wall
thickness estimation and then to the upper value before low-component
extraction. Loading a new volume clears crop state, preventing a stale height
map from being applied to different dimensions.

The same additive state also transports an auxiliary surface for `DepthMap`
and `HeightProjection` when `use_height_map` is set while crop `enabled` is
clear. Those operations validate exactly `width * height` finite samples and
consume the supplied surface directly; segmentation crop bounds remain off.

`dslt_estimate_operation` returns the 72-byte `dslt_work_estimate`. The same
values are exposed as `.NET ProcessingWorkEstimate`; UI code can therefore show
the estimate before starting, while native execution independently enforces the
same limits to prevent bypasses.
