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

Schema-2 v1 evidence requires candidate provenance schema 1.9. The validator
cross-checks its source commit, input voxel type, container, channels, Z
spacing, final DSLT or Watershed operation, actual CPU/CUDA backend, label
output kind, and output dimensions instead of trusting the manifest coverage
fields alone. A Watershed result is accepted only when its final prior step is
a hashed DSLT label result and the Watershed seed hash matches that output. The
validator also recomputes the canonical little-endian int32 label payload
SHA-256 and requires it to match provenance `outputSha256`, cryptographically
linking the sidecar to the candidate labels.

Do not commit private microscopy data. `modern/validation/data/`, local
manifests, and generated reports are ignored by Git.

### Fetch and convert the public PlantSeg candidate cohort

Download the five locked gate candidates. Add `-IncludeSpare` for the sixth
independent acquisition:

```powershell
.\modern\scripts\fetch-public-plantseg-cohort.ps1
```

Convert each HDF5 source into a Workbench input and signed label TIFF using the
spacing and representation stored in
`modern/validation/public-plantseg-cohort.lock.json`:

```powershell
dotnet run --project modern\tools\Dslt.Validation.Prepare -- `
  import-plantseg-hdf5 `
  --input modern\validation\data\public-plantseg-hdf5\Movie3_T00002_crop_gt.h5 `
  --output-volume modern\validation\data\public-plantseg-hdf5\converted\Movie3_T00002_input.tif `
  --output-labels modern\validation\data\public-plantseg-hdf5\converted\Movie3_T00002_reference.tif `
  --spacing-x 0.1625 --spacing-y 0.1625 --spacing-z 0.25 --unit um `
  --voxel-type uint8 --channels 1
```

The conversion can also be applied to every selected case directly from the
lock (use `-Force` only when intentionally regenerating existing local
outputs):

```powershell
.\modern\scripts\prepare-public-plantseg-cohort.ps1
```

The `uint16` and `float32` variants preserve normalized intensity, and a
two-channel variant duplicates the acquired channel only to test HyperStack
interoperability. It must not be described as a native multichannel
acquisition. Source HDF5 files and converted volumes remain ignored local
data; commit only the lock, scripts, path-independent manifests, and reports.

For parameter exploration, create an aligned physical crop from the same locked
HDF5 raw/reference pair. A crop is not a substitute for the final full-volume
gate:

```powershell
dotnet run --project modern\tools\Dslt.Validation.Prepare --configuration Release -- `
  import-plantseg-hdf5 `
  --input modern\validation\data\public-plantseg-hdf5\Movie3_T00002_crop_gt.h5 `
  --output-volume modern\validation\data\public-plantseg-hdf5\crops\Movie3_256x256x64.input.tif `
  --output-labels modern\validation\data\public-plantseg-hdf5\crops\Movie3_256x256x64.reference.tif `
  --spacing-x 0.1625 --spacing-y 0.1625 --spacing-z 0.25 --unit um `
  --voxel-type uint8 --channels 1 `
  --crop-x 400 --crop-y 52 --crop-z 63 `
  --crop-width 256 --crop-height 256 --crop-depth 64
```

### Generate a production DSLT candidate without the UI

`Dslt.Validation.Candidate` loads the same TIFF path as the WPF application and
executes the production `DsltSegmentation` operation through the native C ABI.
Use `--estimate-only` first; it rejects an unsafe memory/work estimate without
starting segmentation:

```powershell
dotnet run --project modern\tools\Dslt.Validation.Candidate --configuration Release -- `
  --input modern\validation\data\public-plantseg-hdf5\crops\Movie3_256x256x64.input.tif `
  --native-directory modern\native\out\build\windows-cuda\Release `
  --backend cuda --estimate-only
```

Run the candidate with an aligned reference and an ignored output base to save
the int32 payload, signed label TIFF, and provenance 1.9 sidecar before metric
evaluation:

```powershell
dotnet run --project modern\tools\Dslt.Validation.Candidate --configuration Release -- `
  --input modern\validation\data\public-plantseg-hdf5\crops\Movie3_256x256x64.input.tif `
  --reference modern\validation\data\public-plantseg-hdf5\crops\Movie3_256x256x64.reference.tif `
  --output-base modern\validation\data\public-plantseg-hdf5\candidates\Movie3_manual-preset `
  --native-directory modern\native\out\build\windows-cuda\Release `
  --backend cuda --gaussian-smoothing-radius 2 --apply-z-gradient `
  --z-gradient-coefficient 10 --z-gradient-exponent 1 `
  --radius 14 --direction-level 2 --kernel gaussian `
  --minimum-c -0.020 --maximum-c -0.008 --c-interval 0.002 `
  --closing-radius 2 --minimum-invalid-structure-area 800 `
  --apply-watershed --watershed-connectivity 6 `
  --reference-background 0 --candidate-background -1
```

Reference existence, dimensions, spacing, and unit are checked before the
expensive operation. Existing output files are also rejected; use
`--force-output` only when intentionally replacing the exact output base. Exit
code `0` means the optional reference metrics passed, `1` means a valid report
failed the v1 thresholds, and `2` means preflight or execution failed. A
release-evidence candidate must be built with the exact 40-hex
`SourceRevisionId`; exploratory local output with an unavailable source identity
cannot be admitted to a schema-2 v1 manifest.

The default DSLT values match the preserved version 1.11 manual preset. Enabled
Gaussian smoothing and Z-gradient correction execute through the same native
engine before segmentation. Provenance 1.9 records each preprocessing
operation, parameters, actual backend, dimensions, and output hash in order.
With `--apply-watershed`, the DSLT label output is hashed as the last prior
processing step, every extracted label is selected as a marker, and Watershed
fills the remaining boundary voxels as the separate final operation described
by the paper and version 1.11 manual.

An exploratory CUDA run on the documented Movie3 crop used the same
preprocessing and segmentation settings except for a single-value DSLT sweep
of `minimum-c = maximum-c = -0.036`. It produced 26/26 foreground objects,
Dice `0.9996525032`, volume difference `0.0001531063`, and HD95 `0` voxels.
This crop was also used to choose the parameter, its development provenance has
no release source identity, and it is therefore tuning evidence only—not an
independent test result or v1.0 release evidence. The released manual preset
remains the UI default; dataset-specific parameters belong in each candidate's
provenance.

### Normalize an external reference mask

Public and laboratory reference masks are often compressed unsigned or binary
TIFF stacks, while the release validator deliberately accepts the legacy DSLT
signed-label interchange format. Normalize a 1- to 16-bit grayscale or indexed
TIFF stack before adding it to a local manifest. Binary masks may also be read
from other WIC-supported formats such as PNG when `--binary` is explicit:

```powershell
dotnet run --project modern/tools/Dslt.Validation.Prepare/Dslt.Validation.Prepare.csproj -- normalize-reference `
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

### Assemble a source-locked manifest

After Workbench has written the candidate label TIFF and provenance 1.9
sidecar, create the first schema-2 case without copying metadata by hand:

```powershell
dotnet run --project modern/tools/Dslt.Validation.Prepare/Dslt.Validation.Prepare.csproj -- add-case `
  --manifest modern\validation\real-data-manifest.local.json `
  --dataset-name leaf-validation-cohort `
  --candidate-source-commit 0123456789abcdef0123456789abcdef01234567 `
  --id acquisition-01 --acquisition-id microscope-run-01 `
  --reference-kind expert `
  --input-volume modern\validation\data\acquisition-01.tif `
  --reference-labels modern\validation\data\acquisition-01.reference.tif `
  --candidate-labels modern\validation\data\acquisition-01.candidate.labels.i16.tif `
  --candidate-provenance modern\validation\data\acquisition-01.candidate.json `
  --representative-real
```

Use `--append` for every later case. The command derives voxel type, container,
channel count, Z spacing, and decoded-input SHA-256 from the candidate
provenance. It verifies provenance schema/source identity, candidate operation
and backend, reference/candidate shape and calibration, decoded candidate-label
SHA-256, and every file hash before atomically writing the manifest. Existing
manifests are not changed without `--append`, duplicate case IDs are rejected,
and a failed append leaves the prior manifest intact.

`--representative-real` is deliberately required on every invocation. It is a
curator assertion, not a classification inferred by the tool.

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
