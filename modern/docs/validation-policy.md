# Validation policy

## Result levels

1. **Synthetic-data validated**: deterministic synthetic fixtures pass on CPU;
   optional GPU results agree with CPU within the tolerances below.
2. **Legacy compared**: the same input and parameter set has been compared with
   an archived legacy binary.
3. **Functionally equivalent**: at least five representative confocal stacks
   meet the real-data release gate.

The project must not claim level 2 or 3 while only level 1 evidence exists.

## Automated acceptance criteria

- Lossless volume and label round trips: bit-identical samples and metadata.
- Deterministic masks and labels: exact voxel equality away from threshold ties.
- Components, bounding boxes, and topology: exact equality.
- Floating CPU/GPU outputs: absolute error <= `1e-5` or relative error <= `1e-4`.
- Repeated open/process/close: no retained native or device allocation.
- Native CPU memory safety: MSVC AddressSanitizer passes 250 repeated C ABI
  create/load/process/copy/destroy cycles.
- Managed/native lifetime: 100 repeated `SafeHandle` processing and disposal
  cycles complete without failure.

## v1.0 real-data gate

- At least five single- and multi-channel stacks across 8-, 16-, and 32-bit
  inputs and multiple Z spacings.
- Dice >= 0.995 against accepted legacy or expert reference masks.
- Equal object count and total segmented volume difference <= 0.5%.
- 95th-percentile Hausdorff distance <= 1 voxel.
