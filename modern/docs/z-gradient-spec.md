# Depth-dependent Z-gradient correction

The source-of-truth behavior is the legacy `Filter3D::applyBC` implementation
in `3DFilter/filter3d.cpp`, not the simplified formula printed in the version
1.11 manual. For voxel `(x, y, z)`, the Workbench computes:

```text
surface = heightMap[x, y] when enabled, otherwise 0
distance = max(0, z - surface)
gain = (1 + coefficient * distance / depth)^exponent
output = clamp((input * gain - minimum) / (maximum - minimum), 0, 1)
```

`depth` is the number of Z slices, matching the legacy `imageZ` divisor. The
height map uses voxel-index Z coordinates. Values above the surface are not
attenuated because negative distances are clamped to zero.

## Parameters and defaults

| Parameter | Workbench contract | Default |
|---|---|---:|
| coefficient | finite, 0 through 10 in WPF; native accepts any finite non-negative value | 10 |
| exponent | finite, 1 through 10 in WPF; native accepts any positive value | 1 |
| minimum / maximum | existing display window; maximum must exceed minimum | 0 / 1 |
| filtered height map | optional; when enabled WPF generates it with the visible height-map parameters before correction | disabled |

The operation is additive ABI v1 ID `DSLT_OP_Z_GRADIENT = 26`. The common
request maps coefficient to `constant_c`, exponent to `threshold`, and the
range-adjustment bounds to `window_min` / `window_max`. An optional `width x
height` surface is passed through the existing versioned crop-state function
with `enabled = 0` and `use_height_map = 1`; no public structure size changes.

CPU is the reference path. CUDA uses the same selected channel, formula, and
optional surface. Float parity is gated by absolute tolerance `1e-5` or
relative tolerance `1e-4`. Cancellation, invalid parameters, height-map shape,
and finite-value checks are covered by native and managed fixtures.

The Workbench records coefficient, exponent, height-map enablement, the
height-map generation parameters, the display window, backend, and input hash.
The derived surface array is deliberately not embedded in JSON because it is
deterministically regenerated from those inputs.
