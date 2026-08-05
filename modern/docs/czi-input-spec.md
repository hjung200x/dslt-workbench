# CZI input contract

## Status and purpose

CZI is an optional, read-only input extension. It is not part of the legacy
DSLT equivalence claim. TIFF/LSM input and every processing backend must remain
usable when the CZI runtime is absent or cannot be initialized.

The decoder uses the pinned ZEISS libCZI dependency recorded in
`validation/libczi-dependency.lock.json`. A DSLT-owned, versioned C ABI isolates
the application from the upstream C++ ABI.

## Phase-one support

- Windows x64 and UTF-8 file paths;
- explicit selection of one scene and one time point;
- one or more explicitly selected channels;
- a complete, non-mosaic, pyramid-layer-zero `C x Z x Y x X` volume;
- `Gray8`, `Gray16`, and `Gray32Float` pixels;
- uncompressed, JPEG-XR, and Zstd-compressed subblocks supported by libCZI;
- channel names and display colors when present;
- finite positive X/Y/Z physical spacing, converted from metres to micrometres;
- progress and cancellation between decoded planes;
- checked allocation sizing before managed or native output allocation.

The decoded internal order is channel-planar `C -> Z -> Y -> X`, matching
`VolumeData` and the native processing core. Raw sample bytes are retained
without normalization. Processing samples reuse the TIFF/LSM type-specific
normalization contract.

## Explicitly unsupported in phase one

- writing CZI;
- simultaneous multi-time-point processing;
- mosaic stitching or multiple M tiles for a selected plane;
- user-selectable pyramid levels;
- packed BGR/RGB pixel types;
- non-unit H, I, V, B, or other acquisition-specific dimensions;
- missing planes, non-rectangular channel coverage, or channel-dependent X/Y/Z
  geometry;
- CZI annotations, attachments, valid-pixel masks, or proprietary analysis
  results.

Unsupported dimensions are rejected with their dimension name and observed
range. The loader must never select index zero silently for a non-unit
dimension. A file with multiple scenes or time points requires an explicit
selection before pixels are read.

## Import selection and source identity

Every import records:

- scene index and scene bounding rectangle;
- time index and timestamp when present;
- original and imported channel indices, names, and colors;
- all fixed dimension coordinates;
- pyramid layer (zero in phase one);
- source pixel type, dimensions, and X/Y/Z spacing;
- SHA-256 of the CZI container;
- SHA-256 of the selected, canonical channel-planar raw samples;
- pinned libCZI source revision.

The application stores only the source file name in portable result metadata;
an absolute local path is not required for reproducibility and is not exported.

## Calibration and Z resampling

The importer never changes voxel spacing or resamples Z. Missing, non-finite,
or non-positive physical sizes produce an uncalibrated volume and a visible
warning. Z area-average or Lanczos resampling remains a separate processing
operation. Its result must contain the computed output spacing rather than the
original CZI spacing.

## Failure and resource policy

Dimension and byte-count products use checked 64-bit arithmetic. Before
allocation, the importer accounts for the selected raw volume, Float32 working
volume, one decoded plane, and native decoder overhead. The existing 75% of
available-memory limit applies.

Cancellation, invalid metadata, unsupported geometry, decoder errors, and
allocation refusal must leave the current source volume and last successful
result unchanged. Native exceptions never cross the C ABI; callers receive a
stable error code and an owned UTF-8 message.

## Completion evidence

Phase-one support is complete only when:

1. generated Gray8, Gray16, and Gray32Float fixtures preserve shape, raw
   samples, channel metadata, and calibration exactly;
2. malformed, truncated, unsupported-axis, missing-plane, and allocation-limit
   fixtures fail closed;
3. native handle, managed `SafeHandle`, cancellation, and repeated-open stress
   tests show no resource growth;
4. WPF selection, cancellation, status preservation, and provenance tests pass;
5. preview packaging includes the CZI runtime and all LGPL/third-party notices;
6. public GitHub-hosted CI passes;
7. all five private Cortex CZI inputs match the GuardVision `czifile` oracle for
   selected raw voxels, `C/Z/Y/X` shape, channel identity, and X/Y/Z spacing;
8. ten repeated imports of each Cortex input show no persistent native-memory
   increase.
