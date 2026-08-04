# Legacy compatibility matrix

Status values: `implemented`, `scaffolded`, `pending-reference`, and `excluded`.

| Legacy capability | Workbench contract | Status | Validation |
|---|---|---:|---|
| Basic multi-page TIFF input | `VolumeDescriptor` + WPF TIFF adapter | implemented | Gray8/Gray16/Gray32Float fixtures plus file-backed 512 x 512 x 32 Gray16 raw-byte/order/performance fixture |
| ImageJ HyperStack metadata | `XYCZT` to channel-planar adapter | implemented | Synthetic 2-channel x 2-slice calibration fixture |
| LSM input | TIFF-compatible WIC pixels + checked CZ_LSMINFO metadata | scaffolded | Tag 34412 magic/size, X/Y/Z/C/T, meter voxel calibration, C-fastest channel-planar order, reduced-resolution IFD filtering, channel names/RGBA colors, timestamps, codec frame-count variants, and malformed optional-block fixtures implemented; real LSM comparison pending |
| 8/16-bit integer and 32-bit float input | Raw decoded samples + normalized float working volume | implemented | Bit-exact decoded sample fixtures |
| 32-bit integer image input | Checked raw TIFF strip path | scaffolded | Signed/unsigned, little/big-endian, multi-page, multi-strip, LZW/Deflate/Adobe Deflate/PackBits, horizontal predictor, LZW EarlyChange dictionary growth, cancellation, bit-exact canonical bytes, normalization, and malformed compressed/uncompressed strip fixtures implemented; real microscopy comparison pending |
| Channel selection | Explicit `channel` in descriptor/request | implemented | Synthetic multichannel fixture |
| Z area-average scaling | `ResampleZArea` | implemented | Ramp/impulse, checked output geometry/resource-limit, WPF parameter/provenance, and result-plane fixtures |
| Z Lanczos 2/3 scaling | `ResampleZLanczos` | implemented | Both orders, checked output geometry/resource-limit, WPF parameter/provenance, and result-plane fixtures |
| Orthogonal XY/YZ/ZX views | `ExtractPlane` + synchronized WPF planes | implemented | Native coordinate fixtures + WPF axis-dimension fixture |
| Brightness/contrast | `WindowLevel` | implemented | Range fixtures |
| Mean/Gaussian smoothing | `SmoothMean`, `SmoothGaussian` | implemented | Impulse fixture |
| Global binary threshold | `Threshold2D`, `Threshold3D` preview IDs | implemented | Boundary fixtures plus WPF 2D active-slice and 3D operation coverage |
| Adaptive 2D/3D threshold | `AdaptiveThreshold2D`, `AdaptiveThreshold3D` | scaffolded | CPU/CUDA mean/Gaussian, clamp boundary, strict tie, 2D/3D distinction, parameter/cancellation/memory, ABI and WPF fixtures implemented; legacy GPU discrepancy and archived-runtime comparison pending |
| Cubic/spherical morphology | `Dilate*`, `Erode*` | implemented | Sphere/shell fixtures plus WPF cube/sphere operation and radius coverage |
| Filtered height map / 3D depth map | `HeightMap`, `DepthMap` | scaffolded | Height-map Gaussian/mean Z filter, strict crossing interpolation, repeated XY smoothing, clamp boundary, plus exact voxel-index Euclidean distance to the height surface; synthetic and cancellation fixtures implemented, legacy runtime comparison pending |
| Height projection and RGB depth coloring | `HeightProjection` + WPF RGB24 presentation | scaffolded | Source-derived Z/normal modes, surface offset, start depth, inclusive range, scalar/binary threshold behavior, trilinear sampling, plus Z-only HSV 0..270 depth coloring with default range 100, provenance, WPF/oracle fixtures, and native-CPU composition gate implemented; archived-runtime comparison pending |
| 6/18/26 connectivity | `ConnectedComponents` | implemented | Touching-object fixtures |
| Flood-fill segmentation | Connected-components request | implemented | Noise/min-size fixtures |
| h-minima transform | `HMinima` | scaffolded | CPU erosion reconstruction, lower-mask fitting, residual inversion, convergence, cancellation, ABI and WPF fixtures implemented; archived-runtime comparison pending |
| DSLT directional threshold basis | Ordered geodesic directions, multi-radius line response, directional C | scaffolded | CPU operation plus direction/weight/interpolation/boundary and X/Y/Z/oblique ramp oracles implemented; CUDA mean/Gaussian, multi-radius, level 1/2, correction, cancellation, resource, and voxel-exact RTX 4060 fixtures complete; legacy-binary oracle pending |
| Sobel-like iterative segmentation | `DsltSegmentation` | scaffolded | CPU and CUDA response reuse, endpoint-inclusive C schedule, closing, wall validation, append-only labels, final pass, cancellation, fixed/height-map crop, checked CPU/VRAM resource estimates, staged-defect fixtures, manual v1.11 defaults, and hashed preprocessing-to-DSLT provenance implemented; one PlantSeg tuning crop passes the four numeric gates, while independent full-volume and legacy-runtime comparisons remain pending |
| Threshold sweep | `ThresholdSweep` | scaffolded | CPU and CUDA descending sweep, closing, fixed/height-map crop, wall thickness, invalid-structure checks, append-only labels, final pass, cancellation, ABI and WPF fixtures implemented; voxel-exact RTX 4060 parity complete, legacy-runtime comparison pending |
| Watershed | `Watershed` | scaffolded | CPU and CUDA selected-seed flooding, fixed 256 levels, legacy 6-neighbor priority, per-level label opening, crop, minimum seed size, cancellation, ABI and WPF undo/provenance fixtures, plus explicit hashed DSLT-seed composition for headless validation implemented; voxel-exact RTX 4060 parity complete, independent full-volume and archived-runtime comparisons pending |
| Segment select/merge/split/crop/dilate/erode/undo | `LabelEditingSession` | implemented | 3D connectivity fixtures; WPF edit fixture; crop dimension/origin/label bit-exact undo fixture |
| Legacy keyboard/mouse gestures | Ctrl+O/Ctrl+S, Shift-click zoom, middle-click selection, Alt+J/Alt+S, Ctrl+Z, Alt+A/Alt+D | implemented | Commands are bound; uniform-image letterbox/pixel mapping and ViewModel selection fixtures pass |
| Mean-intensity segment auto-selection | Active-channel mean threshold with select/deselect mode | implemented | Source-coordinate/cropped-label mapping, strict threshold, select-all, selection and deselection ViewModel fixtures pass |
| Segment erosion clamp | Preserve image-edge voxels when clamp is enabled | implemented | Clamped and unclamped edge-volume fixtures pass; WPF defaults clamp on as in the manual example |
| Depth-dependent Z-gradient correction | `ZGradient`: `(1 + coefficient * max(0, z - surface) / depth)^exponent * intensity`, then range adjustment | scaffolded | Source-derived CPU and CUDA paths, optional filtered height surface, defaults, ABI/P/Invoke mapping, cancellation, provenance, WPF orchestration, and synthetic formula/parity fixtures implemented; archived-runtime comparison pending |
| Segment TIFF load/save | Signed 16-bit compatibility + signed 32-bit extended | implemented | Bit-exact multi-page round trip, calibration, overflow warning, WPF import/edit, geometry rejection with state preservation, legacy sparse-ID/background normalization, working-volume calibration retention, source-label hash, and export fixtures |
| Segment minimum-size display filter | Show labels only when voxel count is strictly greater than the configured minimum | implemented | Source-derived strict `size > seg_minVol` semantics and WPF one-/two-voxel display fixtures; labels and selection remain unchanged |
| Optional CUDA backend | `Auto`, `CPU`, `CUDA` execution policy | scaffolded | Every public C ABI v1 operation has a CUDA path; hosted CUDA 13.2 NVCC/MSVC build plus registered RTX 4060 self-hosted runtime parity covers exact masks/labels, float tolerances, cancellation, validation, and mixed-operation memory fixtures; broader GPU-fleet and legacy-runtime comparison pending |
| Win32/x86 build | None | excluded | Windows x64 policy |

Display-only RGB depth coloring remains a deterministic presentation layer over
the scaffolded scalar projection, height-map, and depth-map contracts; it does
not change the versioned C ABI payload.
