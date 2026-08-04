# Functional specification and traceability

This document separates confirmed legacy behavior from the Workbench preview
contract. Legacy evidence comes from `WpfApplication/MainWindow.xaml`,
`WpfApplication/MainWindow.xaml.cs`, `3DFilter_CLR_Interface/3DFilter_CLR_Interface.h`,
`3DFilter/filter3d.h`, and `MultiTiffIO/multi_tiff.cpp` at the pinned baseline.
The detailed DSLT mathematical and ordering contract is maintained in
[`dslt-algorithm-spec.md`](dslt-algorithm-spec.md). The preserved version 1.11
user manual and its page-by-page evidence are recorded in
[`legacy-manual-audit.md`](legacy-manual-audit.md).

## Input and navigation

| Capability | Legacy evidence | Confirmed default/range | Workbench preview contract |
|---|---|---|---|
| Multi-page TIFF | `set3DImage_MultiTIFF`; `MultiTiffIO::GetImageData` | Channel defaults to 0 | Classic Gray8/Gray16/Gray32Float plus uncompressed signed/unsigned Int32 decoded samples are preserved; raw Int32 supports endian conversion, multiple strips, cancellation, and malformed-range rejection; frames must have equal X/Y size; failed load preserves the active volume |
| ImageJ HyperStack | `TiffDecoder`; page order `XYCZT` | One time point loaded at a time | `C x Z`, unit, and spacing are synthetic-validated; time series are rejected explicitly |
| LSM metadata | TIFF-compatible pixel decode plus Zeiss private tag 34412 | Not fully established | CZ_LSMINFO magic/size, X/Y/Z/C/T, meter-to-micrometer voxel calibration, C-fastest page order, reduced-resolution IFD exclusion, channel names/RGBA colors, and timestamps are synthetic-validated; real LSM comparison remains pending |
| Channel selection | `ch_slider`; `setChannel` | 0 through channel count - 1 | `SelectedChannel` must be within `[0, Channels)` |
| Z interpolation | `SC_AREA_AVE`, `SC_LANCZOS2`, `SC_LANCZOS3` | Area average selected in the legacy UI; target spacing follows input X spacing | WPF exposes area average and Lanczos order 2/3; target spacing must be finite and positive, defaults to calibrated X spacing, and excessive output depth fails before allocation |
| Orthogonal planes | `getImageDataArrayXY/YZ/ZX`; S/D call `saveSrc2DImage`/`saveDst2DImage` | Coordinates start at 0; snapshots emit XY plus `YZ`/`ZX` filename variants | Out-of-range plane indices return an invalid-argument error; WPF can save the currently rendered Source or Result XY/YZ/ZX planes as three TIFF files with legacy-compatible names |
| Brightness/contrast | `BC_min_slider`, `BC_max_slider` | Min 0, max 1 | Window maximum must be greater than minimum |

## Processing and segmentation

| Capability | Legacy parameter evidence | Legacy UI default | Workbench validation |
|---|---|---:|---|
| Global binary threshold | `thresholding(float th)` | Tool-specific | Float threshold; output is 0 or 1 |
| Adaptive 2D/3D threshold | radius, constant C, mean/Gaussian kernel | radius 14, UI C 20, mean | CPU/CUDA XY/XYZ separable convolution, clamp boundary, strict comparison, C mapping, progress/cancellation, VRAM preflight, ABI and WPF controls are synthetic-validated; legacy GPU discrepancy and archived-runtime comparison pending |
| Mean/Gaussian smoothing | filter type, block/radius | Mean is selected in relevant panel | Radius 0-64 in native core |
| Cube/sphere morphology | radius, filter shape | Radius 1 in segment edit panels | Radius 0-64 in native core |
| Flood fill/components | threshold, 6/18/26 connectivity, minimum size | Threshold 0.1, connectivity 6, minimum size 0 | Connectivity is exactly 6, 18, or 26; preview minimum size is at least 1 |
| Height map | XY/Z radius, threshold, mean/Gaussian filter, smooth level | Manual example: XY radius 64, Z radius 4, threshold 0.1, Gaussian, smooth level 1; source-derived preview defaults differ | GPU-visible source path is implemented on CPU: clamp-boundary Z filtering, strict threshold crossing with linear Z interpolation, and repeated separable XY smoothing; ABI/WPF fixtures pass, archived-runtime comparison pending |
| Height-map persistence | H/J read/write binary headers 120/240, width, height, then row-major Float32 | Active surface must match image X/Y | Workbench validates exact length, dimensions and finite values, records SHA-256, preserves state on failure, and reuses the surface for height-relative crop, Z-gradient, surface area, DepthMap, HeightProjection, and RGB depth coloring |
| Height-surface area | active height map, fixed integration resolution | A command; resolution 10 in both axes | Workbench reproduces the source vertex-normal accumulation, four-vertex cell-normal average, bilinear normal-ratio integrand and 10 x 10 two-dimensional Simpson rule in voxel-index units. It displays a normalized Gray8 map and writes paired Gray8/IEEE Float32 TIFF files while recording the input surface hash. See `height-surface-area-spec.md`. |
| Depth map/projection | height map, normal/Z mode, offset, start depth, range, projection threshold, depth-code settings | Z mode selected; offset/range/start/threshold 0; depth code off; depth range 100 | Exact voxel-index 3D Euclidean depth, scalar Z/normal sampling, inclusive range, legacy threshold behavior, direct active/imported surface reuse, and Z-only HSV depth coloring over the same surface are implemented with CPU/CUDA, cancellation, provenance, ABI, and WPF fixtures; archived-runtime comparison remains pending |
| Depth-dependent Z-gradient correction | `applyBC`: coefficient, exponent, optional height surface, brightness min/max | coefficient 10, exponent 1, height-relative correction off | CPU/CUDA use `max(0, z - surface)`, divide by the Z-slice count, apply the source-derived power gain, then range-adjust to 0..1; optional surface generation, cancellation, ABI, WPF, and provenance fixtures are implemented; archived-runtime comparison remains pending |
| H-minima | h, check interval | h 0.1, interval 50 (hidden) | CPU 3x3x3 erosion reconstruction, lower-mask fitting, exact fixed point, residual inversion, progress/cancellation, ABI and WPF controls are synthetic-validated; archived-runtime comparison pending |
| DSLT/Sobel-like | radius, geodesic direction level, Z factor, C sweep, mean/Gaussian kernel | Manual segmentation example: radius 14, level 2, Z factor 0.2, Gaussian, min C 4, max C 10, interval -1, ValidTH 800, closing 2 | CPU threshold, iterative sweep, fixed/height-map crop, public work estimate, resource rejection, and ramp oracles implemented; archived-runtime comparison pending |
| Threshold sweep | min, max, interval, minimum volumes, closing | min 0, effective max 1 after WPF coercion, interval 0.02, hidden minimum volume 0, valid area 100, closing 2 | CPU and CUDA descending sweep, closing, crop, validation, append-only labeling, final-pass acceptance, progress/cancellation, and WPF controls are synthetic-validated; RTX 4060 parity complete, archived-runtime comparison pending |
| Watershed | selected segment seeds, minimum segment volume; hidden stride is passed but unused | stride 0.001 (hidden), minimum size 0 | CPU fixed 256-level flooding, source-order 6-neighbor priority, per-level radius-one label opening, crop, selection, cancellation, ABI/WPF undo, and seed-hash provenance are synthetic-validated; archived-runtime comparison pending |
| Segmentation crop | fixed or height-map-relative upper/lower Z bounds and XY border | Cropping off; height-map-relative mode selected; upper 0, lower 1, border 0 | WPF exposes the source parameters for DSLT segmentation, threshold sweep and watershed. A compatible active `.hmp` is reused; otherwise the configured height map is generated first. The surface array crosses the C ABI only for execution and provenance stores its hash rather than embedding it. |

Several legacy XAML controls contain defaults outside their declared slider
ranges (for example a maximum of 1 with a value of 20). Those values are
recorded as source facts, not copied as valid Workbench defaults. A legacy runtime
capture is required to determine whether WPF coercion or code-behind supplied
the effective value.

The SGL sweep controls are a particularly important case: the visible min/max
labels, control names, declared ranges, negative values, and code-behind sign
conversion conflict. Their effective runtime defaults are intentionally marked
`capture-required`; see `dslt-algorithm-spec.md`.

## Segment editing and persistence

The Workbench label model uses signed 32-bit values with `-1` as background.
`LabelEditingSession` provides label selection, deterministic merge, 6/18/26
connected-component split, crop, dilation, erosion, and bounded undo.
It also preserves the manual's image-edge erosion clamp, select-all/deselect,
active-channel mean-intensity selection, middle-click plane selection, Shift
zoom, and keyboard command bindings.
Crop is a dimension-changing undo transaction; result provenance schema 1.6+
records its output origin in the source coordinate system as structured X/Y/Z
fields in addition to the ordered edit history. Watershed records the complete
seed-label SHA-256 and selected label identifiers without duplicating the seed
volume in JSON.
`LabelTiffCodec` writes legacy-compatible signed 16-bit TIFF whenever all labels
fit and otherwise writes signed 32-bit TIFF with an explicit compatibility
warning. Result packages retain the `.i32.raw` payload and JSON provenance.
The WPF import path requires matching X/Y/Z dimensions but, like legacy
`Filter3D::loadSegData`, does not reject differing calibration metadata. The
working volume remains authoritative and the mismatch is reported. Imported
negative samples become background `-1`; non-negative sparse IDs are compacted
in ascending source-ID order. The decoded pre-normalization label SHA-256 is
retained in the ordered history so later edits remain traceable to their label
input.
`LegacyHeightMapCodec` preserves the little-endian 120/240-header `.hmp`
layout. Imported maps require matching X/Y but do not replace volume
calibration. `ImportHeightMap` is a managed workflow identity rather than a C
ABI operation. The active surface crosses ABI v1 through the existing optional
height-map state and can be consumed directly by `DepthMap` and
`HeightProjection` without regenerating it. `HeightSurfaceArea` is likewise
managed-only and records the active map hash plus its fixed integration
resolution.
See [`tiff-io-spec.md`](tiff-io-spec.md).

## Common error and state rules

- Shape multiplication is checked before managed allocation; native dimensions
  are checked before copying.
- Loading or processing failure does not replace the last valid source/result.
- Native exceptions never cross the C ABI; callers receive a status and UTF-8
  error text.
- Progress callbacks can cancel long CPU operations. Cancellation returns the
  dedicated cancelled status.
- Explicit CUDA requests fail when CUDA is unavailable. `Auto` currently uses
  the CPU reference path until individual CUDA operations pass parity tests.
