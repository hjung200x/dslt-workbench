# Watershed CPU reference contract

## Scope and source evidence

This document fixes the synthetic-validation contract for the legacy selected-
segment Watershed path. The source evidence is `Filter3D::watershed3D` at the
pinned baseline, `watershed3D_kernel`, the radius-one label erosion/dilation
kernels, the C++/CLI adapter, and the WPF Watershed panel.

The legacy UI passes a hidden `stride` value of `0.001`, but
`Filter3D::watershed3D` never reads the argument. Workbench therefore does not
expose a non-functional stride parameter. This is a source fact rather than a
mathematical improvement.

## Seed contract

- Input labels use signed 32-bit values with `-1` as background.
- One or more existing non-negative labels must be selected.
- Only selected labels whose voxel count is greater than or equal to the
  minimum seed size are retained. The legacy comparison removes a seed only
  when `size < minimum`.
- The seed shape must match the active selected-channel volume. A cropped edit
  result cannot seed a full-volume request until it is restored.
- Missing selection, mismatched shape, invalid labels, or a minimum that removes
  every selected seed is rejected before processing so the last valid result is
  preserved.

The C ABI receives seed labels and selected identifiers through
`dslt_set_label_state_i32`. The .NET adapter carries the payload separately from
`OperationParameters`. Provenance schema 1.6 records the canonical little-
endian SHA-256 of the complete seed label array and the ordered selected label
identifiers, without copying the full seed array into JSON.

## Flood schedule and deterministic priority

The normalized intensity schedule is fixed to `i / 256` for `i = 1..256`.
At each level, synchronous passes repeat until no background voxel changes.
An eligible voxel has intensity less than or equal to the current level and
takes the first non-negative neighbor in this exact order:

1. `z - 1`
2. `x - 1`
3. `y - 1`
4. `x + 1`
5. `y + 1`
6. `z + 1`

The original CUDA code contains conflict-handling expressions, but after the
first accepted neighbor it sets distance to one and every later check is gated
by `distance > 1`. Consequently, first-neighbor priority is the observable
source contract; Workbench does not substitute a conventional watershed-line
tie rule.

## Per-level label opening

After convergence at every one of the 256 levels, Workbench applies the same
synchronous radius-one label opening as the legacy CUDA path:

1. a non-background voxel becomes background when the first in-bounds priority
   neighbor has a different label;
2. a background voxel then takes the first non-background priority neighbor.

Out-of-volume neighbors are ignored, so the image boundary is not eroded merely
because it has no outside neighbor. Opening runs even when the current flood
level introduced no new voxel.

## Crop, progress, cancellation, and result state

Legacy crop behavior changes the flood intensity outside the configured XY
border and upper/lower Z interval to the maximum float value. Existing seeds
are not deleted by this intensity mask. Fixed-depth and height-map-relative crop
rules use the shared Workbench crop contract.

Progress is reported across the 256 flood levels and checked during convergence
passes. Cancellation returns the dedicated cancelled status. WPF installs a
successful result into the existing `LabelEditingSession` as one undoable
replacement, preserving the selected label identifiers that still exist.

## Validation status

Synthetic fixtures cover two-seed priority, single-selection filtering,
minimum-size rejection, cancellation, C ABI state transfer, WPF seed transfer,
undo, and provenance. Status remains `scaffolded` until an archived legacy
runtime and representative microscopy data confirm voxel-level equivalence.
