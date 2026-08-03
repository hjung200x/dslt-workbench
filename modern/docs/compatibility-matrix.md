# Legacy compatibility matrix

Status values: `implemented`, `scaffolded`, `pending-reference`, and `excluded`.

| Legacy capability | Workbench contract | Status | Validation |
|---|---|---:|---|
| Basic multi-page TIFF input | `VolumeDescriptor` + WPF TIFF adapter | implemented | Three-frame Gray8 smoke fixture |
| ImageJ HyperStack metadata | TIFF metadata adapter | pending-reference | Metadata round-trip fixtures required |
| LSM input | TIFF-compatible LSM adapter | pending-reference | Real LSM fixture required |
| 8/16/32-bit input | Normalize to float working volume | scaffolded | Gray8 smoke fixture only; remaining type fixtures required |
| Channel selection | Explicit `channel` in descriptor/request | implemented | Synthetic multichannel fixture |
| Z area-average scaling | `ResampleZArea` | implemented | Ramp and impulse fixtures |
| Z Lanczos 2/3 scaling | `ResampleZLanczos` | implemented | Ramp and impulse fixtures |
| Orthogonal XY/YZ/ZX views | `ExtractPlane` | implemented | Coordinate fixtures |
| Brightness/contrast | `WindowLevel` | implemented | Range fixtures |
| Mean/Gaussian smoothing | `SmoothMean`, `SmoothGaussian` | implemented | Impulse fixture |
| Global binary threshold | `Threshold2D`, `Threshold3D` preview IDs | implemented | Boundary fixtures |
| Adaptive 2D/3D threshold | Extended operation contract | pending-reference | Legacy block/C/boundary fixtures |
| Cubic/spherical morphology | `Dilate*`, `Erode*` | implemented | Sphere/shell fixtures |
| Simplified height/depth map | `HeightMap`, `DepthMap` | implemented | Sloped-surface fixture |
| Full height/depth projection controls | Extended operation contract | pending-reference | Legacy projection fixtures |
| 6/18/26 connectivity | `ConnectedComponents` | implemented | Touching-object fixtures |
| Flood-fill segmentation | Connected-components request | implemented | Noise/min-size fixtures |
| h-minima segmentation | `HMinima` | pending-reference | Legacy comparison required |
| Sobel-like directional segmentation | `SobelLike` | pending-reference | Legacy comparison required |
| Threshold sweep | `ThresholdSweep` | scaffolded | Legacy comparison required |
| Watershed | `Watershed` | pending-reference | Legacy comparison required |
| Segment select/merge/split/crop/dilate/erode/undo | `LabelEditingSession` | implemented | 3D connectivity and undo fixtures |
| Segment TIFF load/save | Signed 16-bit compatibility + int32 extended | scaffolded | Round-trip fixtures required |
| Win32/x86 build | None | excluded | Windows x64 policy |

`pending-reference` algorithms are not exposed as completed UI actions. This
prevents an approximation from being mistaken for preserved legacy behavior.
