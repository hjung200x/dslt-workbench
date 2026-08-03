# Legacy compatibility matrix

Status values: `implemented`, `scaffolded`, `pending-reference`, and `excluded`.

| Legacy capability | Workbench contract | Status | Validation |
|---|---|---:|---|
| Basic multi-page TIFF input | `VolumeDescriptor` + WPF TIFF adapter | implemented | Gray8/Gray16/Gray32Float fixtures |
| ImageJ HyperStack metadata | `XYCZT` to channel-planar adapter | implemented | Synthetic 2-channel x 2-slice calibration fixture |
| LSM input | TIFF-compatible WIC pixels + checked CZ_LSMINFO core metadata | scaffolded | Tag 34412 magic/size, X/Y/Z/C/T, meter voxel calibration, C-fastest channel-planar order, reduced-resolution IFD filtering, codec frame-count variants, and malformed magic fixture implemented; channel names/timestamps and real LSM comparison pending |
| 8/16-bit integer and 32-bit float input | Raw decoded samples + normalized float working volume | implemented | Bit-exact decoded sample fixtures |
| 32-bit integer image input | Checked raw TIFF strip path | scaffolded | Signed/unsigned, little/big-endian, multi-page, multi-strip, cancellation, bit-exact canonical bytes, normalization, and malformed byte-count fixtures implemented; compressed Int32 and real microscopy comparison pending |
| Channel selection | Explicit `channel` in descriptor/request | implemented | Synthetic multichannel fixture |
| Z area-average scaling | `ResampleZArea` | implemented | Ramp and impulse fixtures |
| Z Lanczos 2/3 scaling | `ResampleZLanczos` | implemented | Ramp and impulse fixtures |
| Orthogonal XY/YZ/ZX views | `ExtractPlane` + synchronized WPF planes | implemented | Native coordinate fixtures + WPF axis-dimension fixture |
| Brightness/contrast | `WindowLevel` | implemented | Range fixtures |
| Mean/Gaussian smoothing | `SmoothMean`, `SmoothGaussian` | implemented | Impulse fixture |
| Global binary threshold | `Threshold2D`, `Threshold3D` preview IDs | implemented | Boundary fixtures |
| Adaptive 2D/3D threshold | `AdaptiveThreshold2D`, `AdaptiveThreshold3D` | scaffolded | CPU mean/Gaussian, clamp boundary, strict tie, 2D/3D distinction, ABI and WPF fixtures implemented; legacy GPU discrepancy and archived-runtime comparison pending |
| Cubic/spherical morphology | `Dilate*`, `Erode*` | implemented | Sphere/shell fixtures |
| Filtered height map / 3D depth map | `HeightMap`, `DepthMap` | scaffolded | Height-map Gaussian/mean Z filter, strict crossing interpolation, repeated XY smoothing, clamp boundary, plus exact voxel-index Euclidean distance to the height surface; synthetic and cancellation fixtures implemented, legacy runtime comparison pending |
| Scalar height projection controls | `HeightProjection` | scaffolded | Source-derived Z/normal modes, surface offset, start depth, inclusive range, scalar/binary threshold behavior, trilinear sampling, ABI and WPF fixtures implemented; RGB depth coloring and archived-runtime comparison remain pending presentation/reference work |
| 6/18/26 connectivity | `ConnectedComponents` | implemented | Touching-object fixtures |
| Flood-fill segmentation | Connected-components request | implemented | Noise/min-size fixtures |
| h-minima transform | `HMinima` | scaffolded | CPU erosion reconstruction, lower-mask fitting, residual inversion, convergence, cancellation, ABI and WPF fixtures implemented; archived-runtime comparison pending |
| DSLT directional threshold basis | Ordered geodesic directions, multi-radius line response, directional C | scaffolded | Scalar CPU operation plus direction/weight/interpolation/boundary and X/Y/Z/oblique ramp oracle fixtures implemented; legacy-binary oracle pending |
| Sobel-like iterative segmentation | `DsltSegmentation` | scaffolded | CPU response reuse, C schedule, closing, wall validation, append-only labels, final pass, cancellation, fixed/height-map crop, checked resource estimates, and staged-defect fixtures implemented; legacy runtime comparison pending |
| Threshold sweep | `ThresholdSweep` | scaffolded | CPU descending sweep, closing, crop, validation, append-only labels, final pass, cancellation, ABI and WPF fixtures implemented; legacy-runtime comparison pending |
| Watershed | `Watershed` | scaffolded | CPU selected-seed flooding, fixed 256 levels, legacy 6-neighbor priority, per-level label opening, crop, minimum seed size, cancellation, ABI and WPF undo/provenance fixtures implemented; archived-runtime comparison pending |
| Segment select/merge/split/crop/dilate/erode/undo | `LabelEditingSession` | implemented | 3D connectivity fixtures; WPF edit fixture; crop dimension/origin/label bit-exact undo fixture |
| Segment TIFF load/save | Signed 16-bit compatibility + signed 32-bit extended | implemented | Bit-exact multi-page round trip, calibration, and overflow warning fixtures |
| Optional CUDA backend | `Auto`, `CPU`, `CUDA` execution policy | scaffolded | Hosted CUDA 13.2 NVCC/MSVC build gate plus copy, window/level, and global 2D/3D threshold parity, cancellation, explicit unsupported-operation, VRAM preflight, and repeated-allocation fixtures implemented; remaining processing groups and self-hosted NVIDIA runtime evidence pending |
| Win32/x86 build | None | excluded | Windows x64 policy |

`pending-reference` algorithms are not exposed as completed UI actions. This
prevents an approximation from being mistaken for preserved legacy behavior.
Display-only RGB depth coloring remains separate from the scaffolded scalar
projection contract.
