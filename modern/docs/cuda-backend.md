# CUDA backend contract

The CPU backend remains the normative implementation. CUDA is selected only
for operations that have an automated CPU parity fixture on a real NVIDIA
runner. Unsupported operations are never reported as CUDA-complete.

## Validated pointwise group

The first CUDA group contains:

- selected-channel copy;
- window/level with the same clamp-to-`[0, 1]` rule as CPU;
- global 2D threshold;
- global 3D threshold.

`Auto` tries CUDA for these operations when device discovery succeeds. A CUDA
initialization failure returns to the CPU implementation. An explicit `CUDA`
request instead returns the CUDA error. Unsupported explicit requests return
`DSLT_NOT_IMPLEMENTED`.

## Resource and lifetime rules

- Input and output device bytes are checked against `cudaMemGetInfo` before
  allocation. An insufficient request reports required and available bytes and
  does not launch a kernel.
- Each invocation owns a nonblocking stream and two RAII device buffers. No
  global texture/context state or `cudaDeviceReset()` is used.
- Host-to-device copy, kernel launch, device-to-host copy, and stream
  synchronization are checked explicitly.
- Cancellation is checked before upload and after synchronization. A cancelled
  result is not published.

## Parity gate

The NVIDIA self-hosted test compares copy and binary threshold bit-exactly.
Window/level must satisfy absolute error `<= 1e-5`. One hundred repeated
threshold invocations must leave reported free device memory within a 64 MiB
runner-noise allowance. The broader project tolerance remains absolute
`<= 1e-5` or relative `<= 1e-4` for future floating-point kernels.

The CUDA CMake configuration defines `DSLT_TEST_REQUIRE_CUDA` for the native
contract test. A CUDA build therefore fails if the library was compiled without
CUDA support or if CUDA runtime initialization cannot see a device; it cannot
pass by exercising the normal unavailable-backend contract.

The self-hosted runner must carry `self-hosted`, `Windows`, `X64`, and `NVIDIA`
labels and provide an NVIDIA GPU/driver, CUDA Toolkit 13.2, Visual Studio 2022
C++ tools, CMake 3.30 or newer, and .NET 10. Its workflow records the GPU,
driver, toolchain, and source commit in the run summary. Only after parity and
memory-lifetime tests succeed does it build, verify, and upload a self-contained
CUDA preview ZIP with its SHA-256 checksum.

Run `modern/scripts/test-cuda-prerequisites.ps1` from PowerShell before
registering a machine as a runner. It reports every missing prerequisite in one
pass and exits unsuccessfully until the machine satisfies the same checks used
by CI.
