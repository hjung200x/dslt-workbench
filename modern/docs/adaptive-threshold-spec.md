# Adaptive threshold compatibility specification

## Authority and status

This contract follows the pinned legacy CPU functions
`AdaptiveThreshold2D_CPU` and `AdaptiveThreshold3D_CPU` together with the
reference convolution and binarization functions in
`ConvolutionSeparableGPU/convolutionSeparable_gold.cpp`. The Workbench CPU
implementation is synthetic-data validated and is the behavioral reference
for the matching CUDA implementation. Archived-runtime comparison is still
required, so the compatibility status remains `scaffolded`.

The legacy CLR selects a GPU implementation whenever CUDA is available. Its
`AdaptiveThreshold2D_GPU` path changes the evaluated axes and C multiplier for
some radius ranges, while the CPU path is a stable XY-only definition. CPU is
the Workbench reference backend; this discrepancy must be resolved with an
archived executable and real fixtures before claiming legacy equivalence.

## Mathematical contract

For radius `r`, the odd kernel length is `2r + 1`. Mean weights are uniform.
Gaussian weights use:

```text
sigma = 0.3 * (r - 1) + 0.8
w(k)  = exp(-k^2 / (2 * sigma^2)), normalized over k=-r..r
```

Each axis is convolved separately. Coordinates outside the volume are clamped
to the nearest edge voxel. The 2D operation evaluates X then Y independently
for every Z slice. The 3D operation evaluates X, Y, then Z. After the local
response `L(p)` is calculated:

```text
B(p) = 0.8 if I(p) > L(p) - C, otherwise 0.0
```

The comparison is strict, so equality produces `0.0`. Radius zero is valid and
therefore compares each voxel with itself. Workbench accepts radius `0..100`,
kernel `Gaussian` or `Mean`, and a finite core C value.

## Legacy UI mapping

The legacy local-threshold panel declares radius `0..100` with default `14`, C
`-100..500` with default `20`, and selects the mean kernel. Code-behind negates
the visible C and the CLR bridge multiplies it by `0.002`:

```text
core C = -0.002 * uiOffset
```

Thus the default comparison is `I > L + 0.04`. Workbench exposes both 2D and
3D operations; the old 2D button was present but hidden in XAML.

## ABI v1 mapping

The operation IDs are additive and do not change any v1 structure size:

- `DSLT_OP_ADAPTIVE_THRESHOLD_2D = 21`
- `DSLT_OP_ADAPTIVE_THRESHOLD_3D = 22`

| C ABI field | Adaptive threshold meaning |
|---|---|
| `radius` | Local-kernel radius |
| `connectivity` | Kernel type: 0 Gaussian, 1 mean |
| `constant_c` | Core C after UI sign/scale conversion |

The managed adapter exposes `AdaptiveThresholdKernel`; WPF maps the visible
legacy offset into `OperationParameters.ConstantC` for reproducible provenance.

## Synthetic acceptance fixtures

- independent mean and Gaussian separable oracle with edge-clamped coordinates
- 2D/3D distinction on a `1 x 1 x 3` Z impulse
- strict tie at radius zero
- invalid radius, invalid kernel, non-finite C, and cancellation
- voxel-exact CPU/CUDA parity for both axes modes, both kernels, and positive
  and negative C values
- repeated CUDA execution without measured device-memory growth
- C ABI and managed-to-native output checks
- WPF default and parameter-mapping checks
