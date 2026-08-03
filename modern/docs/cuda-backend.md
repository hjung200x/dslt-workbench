# CUDA backend contract

The CPU implementation is the behavioral reference. CUDA operations must use
the same selected channel, parameter interpretation, boundary rules, output
shape, and output kind as CPU.

## Selection and failure policy

- `CPU` always runs the CPU reference implementation.
- `CUDA` fails with `DSLT_BACKEND_UNAVAILABLE` when no usable device exists and
  `DSLT_NOT_IMPLEMENTED` when the requested operation has not been ported.
- `Auto` uses CUDA only for a ported operation on an available device. It uses
  CPU when CUDA initialization is unavailable or the operation is not ported.
- Invalid parameters, cancellation, insufficient memory, and kernel/runtime
  failures are returned to the caller. They do not silently rerun on CPU.

The currently ported group is `Copy`, `WindowLevel`, `Threshold2D`, and
`Threshold3D`.

## Resource ownership and execution

Each request owns a non-blocking CUDA stream plus its input and output device
buffers. RAII destructors release all resources; the backend never calls
`cudaDeviceReset()` and does not use global texture or context state.

Before allocation, the backend checks size arithmetic and compares the two
required float buffers with `cudaMemGetInfo`. Allocation and runtime failures
are converted into DSLT error states and UTF-8 diagnostic text. Every kernel
launch is checked with `cudaGetLastError`, and the stream is synchronized
before host output is published.

Progress is reported at start, input transfer, execution, and completion.
Cancellation before a completed synchronization drains the owned stream and
discards the partial result.

## Validation gate

The `windows-cuda` preset defines `DSLT_TEST_CUDA` and requires a usable NVIDIA
device. Its native test suite checks:

- CPU/CUDA output parity for every ported operation;
- `Auto` selecting CUDA for a ported operation;
- explicit rejection of an unported operation;
- cancellation propagation; and
- 100 repeated operations without more than 1 MiB apparent free-memory drift
  after kernel warm-up.

Pointwise float parity uses an absolute tolerance of `1e-6`, which is stricter
than the project-wide float gate (`abs <= 1e-5` or `rel <= 1e-4`). Binary
threshold results are therefore voxel-exact on the test fixture.
