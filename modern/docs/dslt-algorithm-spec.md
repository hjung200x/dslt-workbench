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

Implementation status: **scalar CPU threshold basis implemented; full fixture
set and iterative segmentation pending**. The initial operation covers ordered
directions, every-radius response, trilinear clamp sampling, direction-dependent
C, and strict binarization. Closing, validation, and the C sweep remain in
Stage 7.

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
addressing (`linearConvolution_Texture.cu:73-101`). CPU and future CUDA code
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
| `kernelType` | 0 Gaussian, 1 mean | Mean selected | Closed enum `Gaussian` or `Mean`; default `Mean` |
| `zCorrectionFactor` | Multiplies `Cxy` to obtain `Cz` | 0..1, default 0.2 | Finite float >= 0; default 0.2 |
| preview `C` | Negated, then scaled by 0.002 | 0..200, default 20 | Finite positive UI offset; default 20; adapter maps to core C values |
| sweep C bounds | CLR scales all values by 0.002; core requires `minC <= maxC` | XAML names/labels/signs conflict | Core receives increasing finite `Cxy` values and a positive interval; UI compatibility is `capture-required` |
| `closing` | Spherical maximum then minimum filter | 0..50, default 2 | Integer >= 0; default 2 |
| `validArea` | Converted to volume by multiplying estimated wall thickness | 0..10000, default 500 | Integer >= 0; exact validation rule remains `capture-required` |

Levels can become computationally explosive. The Workbench must calculate
direction count, sample count, host memory, and estimated work with checked
arithmetic before starting. A resource-limit error must report the estimate;
it must not silently lower the requested level.

The legacy segmentation XAML declares values that conflict with slider ranges
and swaps visible min/max labels (`MainWindow.xaml:694-728`). WPF coercion and
the intended installed-binary defaults cannot be inferred reliably from source
alone. Those sweep defaults remain `capture-required`.

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

Watershed is **not** called by this function. It is a separate selected-segment
editing action (`filter3d.cpp:6877-6925`, `MainWindow.xaml.cs:1591-1595`). The
paper-level end-to-end workflow may include watershed, but Workbench must not
fold it into the DSLT threshold operation implicitly.

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
pass. CUDA must later pass the project-wide numerical tolerances against these
CPU results.

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
not need to know the C-field reuse. The planned UI-facing positive threshold
offset adapter is not exposed yet.
