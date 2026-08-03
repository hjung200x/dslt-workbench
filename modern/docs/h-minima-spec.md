# H-minima compatibility specification

## Authority and status

The Workbench contract follows `Filter3D::hMinimaTransform3D_GPU` and the
minimum-filter, lower-mask-fitting, threshold, and inverse kernels in
`ConvolutionSeparableGPU/morphologicalReconstruction.cu`. The CPU operation is
synthetic-data validated. Archived-runtime comparison remains required, so the
compatibility matrix reports `scaffolded`.

## Reconstruction contract

For source volume `I` and height `h`, initialize marker `R0 = I + h`. Repeatedly
apply a radius-one minimum filter along X, Y, and Z, followed by lower-mask
fitting:

```text
E(R)       = min over the 3 x 3 x 3 neighborhood
R(next)(p) = max(E(R)(p), I(p))
```

Out-of-volume samples are positive infinity and therefore do not affect the
minimum. This is equivalent to ignoring missing neighbors. Iteration stops at
the exact float fixed point. The legacy GPU copies a checkpoint to the host
every `checkInterval` iterations; Workbench retains and validates that
parameter, while the CPU path may detect an unchanged consecutive iteration
early because it produces the same fixed point.

The final residual and legacy binary inversion are:

```text
D(p) = R(p) - I(p)
B(p) = 0.8 if D(p) < 0.00001, otherwise 0.0
```

Workbench accepts finite `h` in `0..1`, check interval `1..10000`, and finite
input voxels. Marker overflow and non-convergence within the checked
volume-derived iteration bound return explicit errors. Progress and
cancellation are evaluated during every reconstruction axis and output pass.

## UI and ABI v1 mapping

The legacy UI declares `h` in `0..1` with default `0.1`. Its hidden convergence
check interval has default `50`. Workbench exposes both values so a result
package records the complete request.

`DSLT_OP_H_MINIMA = 23` is additive and does not change any ABI v1 structure
size:

| C ABI field | H-minima meaning |
|---|---|
| `threshold` | Height h |
| `radius` | Convergence check interval |

The .NET adapter exposes semantic `HMinimaHeight` and
`HMinimaCheckInterval` properties so application code does not depend on C
field reuse.

## Synthetic acceptance fixtures

- deep one-dimensional pit retained at two check intervals
- `h = 0` strict residual boundary
- shallow pit suppressed when `h` exceeds its depth
- isolated 3D center pit with exact foreground/background mask
- invalid h, invalid interval, non-finite source, and cancellation
- C ABI, managed-to-native, and WPF default/parameter mapping
