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

## Display-only legacy options

Legacy depth coloring, segment overlays, and RGB composition change display
pixels rather than the scalar processing result. They are intentionally kept
outside the versioned native operation. The WPF renderer may add these as a
separate presentation feature without changing the scalar projection or its
provenance.

Synthetic fixtures cover lateral 3D distance, Z and flat-normal projection,
binary normal thresholding, scalar Z thresholding, cancellation, invalid
parameters, C ABI mapping, managed mapping, and WPF defaults. Status remains
`scaffolded` until archived-runtime and real microscopy comparisons are
available.
