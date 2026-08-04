# Height-surface area contract

The legacy unmodified A command calls `calcHmapArea(10)`. Workbench preserves
that fixed resolution and treats the active height map in voxel-index units;
calibration spacing is deliberately not applied because the original function
does not use it.

## Source-derived calculation

For each height-map vertex, Workbench accumulates the same four quadrant
vectors as `3DFilter/filter3d.cpp` and normalizes the sum. The fourth quadrant's
X sign is intentionally retained even though it makes the normal accumulation
asymmetric; changing it would alter legacy results. Each cell normal is the
arithmetic mean of its four normalized vertex normals and is not renormalized.

For each interior output location, four adjacent cell normals are bilinearly
interpolated. If the interpolated components are `(nx, ny, nz)`, the integrand
is:

```text
sqrt(1 + (nx / nz)^2 + (ny / nz)^2)
```

Workbench integrates this function over the unit square with the original
two-dimensional composite Simpson rule at 10 intervals per axis. The value is
stored at `(x + 1, y + 1)` and the outer Float32 border is zero. A flat surface
therefore has unit-valued interior pixels. A source-derived `z=x` plane has
`sqrt(1.25)` away from boundaries because of the preserved quadrant signs.

## Presentation and files

The result view and first TIFF normalize `(area - 1) / (maximum - 1)` to Gray8;
flat surfaces deterministically produce an all-zero preview. Preview borders
represent the legacy unit-area border and therefore normalize to zero. This
also removes the original left-border indexing defect without changing any
quantitative interior or saved Float32 value.

Choosing `area_map.tif` writes:

- `area_map.tif`: normalized Gray8 presentation;
- `area_map32.tif`: uncompressed little-endian classic TIFF with one IEEE
  Float32 sample per pixel (`SampleFormat=3`).

Other chosen stems follow the same `<stem>.tif` and `<stem>32.tif` rule. The
WPF command runs calculation and encoding off the UI thread, supports
cancellation, preserves the previous result on failure, and records the active
surface SHA-256 plus integration resolution in result provenance.
