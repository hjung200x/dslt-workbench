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
4. Set schema 2 `candidateSourceCommit` to the exact 40-hex `sourceCommit`
   from the release candidate's `BUILD-INFO.json`. Every candidate provenance
   sidecar must contain the same commit.
5. Fill every SHA-256 field. `inputFileSha256` hashes the input container;
   `inputDecodedSha256` must equal `inputSha256` in the candidate provenance and
   therefore locks the decoded channel-planar samples used for processing.
6. Set the reference and candidate background labels independently. All other
   label values are foreground object identifiers.
7. Set `container` to `tiff` or `lsm`; it must agree with provenance
   `inputContainer`.

Schema-2 v1 evidence requires candidate provenance schema 1.8. The validator
cross-checks its source commit, input voxel type, container, channels, Z
spacing, DSLT segmentation operation, actual CPU/CUDA backend, label output
kind, and output dimensions instead of trusting the manifest coverage fields
alone. It also recomputes the canonical little-endian int32 label payload
SHA-256 and requires it to match provenance `outputSha256`, cryptographically
linking the sidecar to the candidate labels.

Do not commit private microscopy data. `modern/validation/data/`, local
manifests, and generated reports are ignored by Git.

### Normalize an external reference mask

Public and laboratory reference masks are often compressed unsigned or binary
TIFF stacks, while the release validator deliberately accepts the legacy DSLT
signed-label interchange format. Normalize a 1- to 16-bit grayscale or indexed
TIFF stack before adding it to a local manifest. Binary masks may also be read
from other WIC-supported formats such as PNG when `--binary` is explicit:

```powershell
dotnet run --project modern/tools/Dslt.Validation.Prepare/Dslt.Validation.Prepare.csproj -- `
  --input D:\cohort\expert-mask.tif `
  --output modern\validation\data\acquisition-01.reference.tif `
  --spacing-x 0.25 --spacing-y 0.25 --spacing-z 1.0 --unit um
```

Use `--binary` when every non-background source value is foreground. The
default source/output background is `0` and the default foreground label is
`1`; all three labels can be set explicitly. The tool reports source and output
SHA-256 values and refuses to replace an existing output unless `--force` is
provided. Keep the original reference file and its license/citation alongside
the private cohort records so normalization remains auditable.

Normalization only changes the interchange encoding. It does not make a public
benchmark representative of the DSLT leaf-imaging use case and does not satisfy
the v1.0 gate without curator confirmation.

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
