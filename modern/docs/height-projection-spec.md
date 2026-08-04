# Height projection and depth-map CPU reference contract

## Shared filtered surface

`DepthMap` and `HeightProjection` first generate the surface defined in
[`height-map-spec.md`](height-map-spec.md). They therefore share its XY radius,
Z radius, threshold, mean/Gaussian kernel, smoothing-pass defaults, clamp
boundary, strict crossing, and no-crossing fallback. The selected channel is
the scalar source for every subsequent sample.

## Depth map

For every voxel above its local surface, the depth value is the minimum
three-dimensional Euclidean distance to any height-surface point:

`D(x,y,z) = min(hx,hy) sqrt((x-hx)^2 + (y-hy)^2 + (z-H(hx,hy))^2)`.

Voxels on or below their local surface are zero. The CPU reference evaluates
the exact squared-distance minimum with separable one-dimensional lower
envelopes, followed by one square root. Distances use voxel-index coordinates,
matching the legacy CPU routine; calibration spacing is not applied.

## Height projection

The scalar output is one `width x height` float image. Defaults are Z mode,
surface offset `0`, start depth `0`, inclusive range `0`, and projection
threshold `0`.

Z mode samples `(x, y, H(x,y) + start + step + offset)` for every integer
`step` from `0` through `range`. Sampling is linear along Z and the maximum is
returned. Samples below Z zero are skipped; sampling stops above the last
slice. A positive projection threshold zeros a maximum below that threshold.

Normal mode derives the source-compatible averaged surface normal and samples
trilinearly along that normal. With projection threshold zero it returns the
maximum. With a positive threshold it preserves the legacy first-hit binary
behavior: the result is one if any sampled value is strictly greater than the
threshold, otherwise zero. Sampling stops when the normal ray leaves the
volume.

Offsets and start depth are clamped by the WPF layer to `-(depth-1)..depth-1`;
range is clamped to `0..depth-1`; thresholds are clamped to `0..1`. The native
boundary rejects invalid modes, negative ranges, and non-finite values.

## Display-only depth coloring

Legacy depth coloring is available only for Z projection. It remains outside
the versioned native scalar operation: Workbench first requests
`HeightProjection`, `HeightMap`, and `DepthMap` from the selected CPU/CUDA
backend, then the WPF presentation layer composes one RGB24 XY image. A failure
or cancellation in any of the three requests leaves the previous scalar result
and RGB presentation unchanged.

Depth coloring defaults off. Its range defaults to 100 and is validated as an
integer from 1 through the legacy maximum 500. The legacy slider admitted zero,
but the original `proj_depth / d_range` expression is undefined at zero, so
Workbench rejects that unsafe endpoint instead of guessing a color.

For the source sample that first establishes the Z-projection maximum, the
color depth is sampled linearly from `DepthMap` at
`H(x,y) + start + step`, deliberately excluding the display surface offset as
the legacy routine does. Depth `0..range` maps to HSV hue `0..270` degrees,
saturation is one, and the scalar projection value is the HSV brightness.
Values deeper than the range clamp to 270 degrees. RGB channels use the
legacy truncating float-to-byte conversion. A thresholded scalar value of zero
therefore remains black regardless of hue.

`depthColorEnabled` and `depthColorRange` are serialized with the ordinary
operation parameters in provenance schema 1.10. The exported processing payload
remains the scalar Float32 projection, so enabling the presentation feature
does not change C ABI output or quantitative processing results.

Synthetic fixtures cover lateral 3D distance, Z and flat-normal projection,
binary normal thresholding, scalar Z thresholding, depth hue/brightness,
offset-independent depth sampling, unsafe parameter rejection, cancellation,
invalid parameters, C ABI mapping, managed mapping, and WPF defaults. The
native-CPU CI job also composes exact RGB bytes from real CPU height,
depth-map, and projection outputs. Status remains `scaffolded` until
archived-runtime and real microscopy comparisons are available.
