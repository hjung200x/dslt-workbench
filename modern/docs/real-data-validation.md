# Real-data validation

`Dslt.Validation` evaluates Workbench label TIFF output against an accepted
legacy or expert label TIFF and emits a machine-readable v1.0 gate report. It
does not infer that a file is biologically representative: the dataset curator
must confirm `dataClassification: representative-real` and the stated
`referenceKind`.

## Prepare a cohort

1. Copy `modern/validation/real-data-manifest.example.json` to a local manifest.
2. Provide at least five distinct acquisitions covering single- and
   multi-channel inputs, `uint8`, `uint16`, and `float32`, and at least two Z
   spacings.
3. For every acquisition, retain the original input, accepted reference label
   TIFF, Workbench candidate label TIFF, and Workbench JSON provenance sidecar.
4. Fill every SHA-256 field. `inputFileSha256` hashes the input container;
   `inputDecodedSha256` must equal `inputSha256` in the candidate provenance and
   therefore locks the decoded channel-planar samples used for processing.
5. Set the reference and candidate background labels independently. All other
   label values are foreground object identifiers.
6. Set `container` to `tiff` or `lsm`; it must agree with provenance
   `inputContainer`.

Candidate provenance schema 1.5 is required. The validator cross-checks its
input voxel type, container, channels, Z spacing, DSLT segmentation operation,
actual CPU/CUDA backend, label output kind, and output dimensions instead of
trusting the manifest coverage fields alone. It also recomputes the canonical
little-endian int32 label payload SHA-256 and requires it to match provenance
`outputSha256`, cryptographically linking the sidecar to the candidate labels.

Do not commit private microscopy data. `modern/validation/data/`, local
manifests, and generated reports are ignored by Git.

## Run

```powershell
dotnet run --project modern/tools/Dslt.Validation/Dslt.Validation.csproj `
  -- modern/validation/real-data-manifest.local.json `
  --output modern/validation/reports/v1-report.json
```

Exit code `0` means all case metrics and cohort coverage passed. Exit code `1`
means the report was produced but the v1.0 gate failed. Exit code `2` means the
manifest or files could not be evaluated.

## Metric definitions

- Dice compares foreground occupancy and is independent of numeric label IDs.
- Object count is the number of same-label 3D connected components using the
  case's 6, 18, or 26 connectivity.
- Segmented volume uses foreground voxel count times calibrated voxel volume;
  the release comparison is the absolute count difference divided by the
  reference foreground count.
- HD95 extracts face-adjacent foreground surfaces, computes exact Euclidean
  distances in voxel coordinates with a separable 3D distance transform, takes
  the nearest-rank 95th percentile in each direction, and reports the larger
  directed value.
- Two empty masks agree. Exactly one empty mask has undefined HD95 and fails.

The v1.0 thresholds are Dice `>= 0.995`, equal object count, segmented-volume
difference `<= 0.5%`, and HD95 `<= 1 voxel`. The report also records the
manifest SHA-256 so the evaluated cohort definition is immutable evidence.
