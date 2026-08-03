# Filtered height-map CPU reference contract

## Source path and defaults

The WPF Height Map panel calls `Class1::generateHeightMap` with XY radius, Z
radius, threshold, mean/Gaussian type, and smooth level. When CUDA is available,
the adapter selects `Filter3D::generateHeightMapGPU`; this is the visible legacy
path and is the Workbench CPU reference contract.

The legacy UI defaults are:

- XY radius: `0`
- Z radius: `4`
- threshold: `0.25`
- kernel: Gaussian
- XY smoothing passes: `1`

Radii are restricted to `0..64` and smooth level to `0..10`.

## Kernel and boundary

Mean weights are uniform over `2r + 1` samples. Gaussian weights are normalized
from

`exp(-offset^2 / (2 sigma^2))`, where `sigma = 0.3 (r - 1) + 0.8`.

The legacy CUDA convolution replicates the nearest edge sample in each halo.
Workbench therefore uses clamp boundaries. The selected channel is filtered
only along Z before the surface search.

## Surface crossing

For every `(x, y)` column, scan from `z = 0` and select the first filtered value
strictly greater than the threshold. A crossing at the first slice returns
`0`. Otherwise the sub-voxel surface is

`z - 1 + (threshold - value[z-1]) / (value[z] - value[z-1])`.

If no value crosses, the surface is `depth - 1`. Output values are Z indices,
not calibrated physical distances.

## XY smoothing

After crossing, the same one-dimensional kernel is applied in X and then Y to
the height image. This separable pass repeats exactly `smooth level` times.
Radius zero is an identity pass but still preserves the configured semantic
value in provenance.

## Legacy divergence and status

The source-only CPU fallback smooths X, Y, and Z before crossing and ignores the
smooth-level argument. The normal WPF path chooses the GPU implementation when
available. Workbench uses that GPU-visible behavior for its complete CPU
backend and records the divergence rather than mixing the two algorithms.

Synthetic fixtures cover sub-voxel crossing, no-crossing fallback, clamp-based
mean XY smoothing, parameter validation, C ABI output, managed mapping, and WPF
defaults. Status remains `scaffolded` until archived-runtime and real microscopy
comparison are available.
