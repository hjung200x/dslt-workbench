# Full-volume 3D reference review protocol

Protocol ID: `dslt-full-volume-reference-review-v1`

## Purpose

This protocol defines the minimum human evidence required before a label TIFF
may be declared an accepted `legacy` or `expert` reference for the DSLT
Workbench v1.0 real-data gate. It does not approve any dataset by itself.

The reference must cover one complete representative Arabidopsis leaf
acquisition. A crop, selected Z planes, 2.5D polygon package, surface mesh,
model prediction, or candidate Workbench output is not a full-volume reference.

## Independence and custody

1. Record the acquisition ID, original container SHA-256, decoded input
   SHA-256, dimensions, channels, selected channel, voxel type, and calibration.
2. Create or recover the reference without using the Workbench candidate as
   ground truth. Keep the candidate hidden until the reference TIFF and review
   log have been frozen and hashed.
3. For an expert reference, record the reviewer identity and the annotation
   tool/version. For a legacy reference, additionally retain the executable
   hash, runtime environment, complete parameters, and unedited legacy output.
4. Do not replace a frozen reference in place. Corrections create a new file,
   new SHA-256, new review log, and new acceptance record.

## Label-volume contract

- The reference is a voxel-aligned 3D TIFF with exactly the input X/Y/Z
  geometry and calibration.
- One background value is declared explicitly. Every other integer is an
  instance identifier.
- Each biological object has one identifier. Reusing an identifier for
  disconnected objects is prohibited unless the selected 6/18/26 connectivity
  deliberately defines them as one object and the review log explains why.
- The reviewer chooses and records one boundary representation for the entire
  case: explicit background wall voxels or directly touching positive labels.
  Mixed accidental representations are corrected before acceptance.
- The choice must be evaluated against the locked metric contract: Dice and
  HD95 compare binary `label != background` occupancy, while object count uses
  same-label connected components. Positive-to-positive label transitions are
  not HD95 surfaces.
- Uncertain or excluded regions remain explicit in the review log. They must
  not be silently assigned to a convenient label to improve candidate metrics.

## Required review passes

1. **Geometry pass:** confirm dimensions, orientation, slice order, spacing,
   background label, and absence of truncated or duplicated planes.
2. **Complete Z pass:** inspect every Z plane at useful zoom, recording all
   ambiguous ranges and corrections.
3. **Orthogonal pass:** inspect XZ and YZ views throughout the volume for
   discontinuities, merged cells, split cells, holes, and one-plane artifacts.
4. **Instance pass:** compute same-label component counts with the manifest
   connectivity; resolve unintended disconnected identifiers and document
   intentional exceptions.
5. **Boundary pass:** inspect positive-to-positive interfaces and background
   walls, then run `audit-reference` and retain its JSON output.
6. **Blind freeze:** save the reference and review log, compute SHA-256, and
   only then reveal or generate the Workbench candidate.
7. **Acceptance pass:** confirm the entire 3D volume, representative-leaf
   classification, and boundary representation by creating the hash-bound
   acceptance record.

## Acceptance command

```powershell
dotnet run --project modern/tools/Dslt.Validation.Prepare/Dslt.Validation.Prepare.csproj -- `
  create-reference-acceptance `
  --reference-labels D:\cohort\acquisition-01.reference.tif `
  --output D:\cohort\acquisition-01.reference.acceptance.json `
  --acquisition-id acquisition-01 `
  --reference-kind expert `
  --accepted-by "reviewer identity" `
  --accepted-at-utc 2026-08-04T12:00:00Z `
  --protocol-id dslt-full-volume-reference-review-v1 `
  --whole-volume-3d-coverage-confirmed `
  --representative-leaf-confirmed `
  --boundary-representation-reviewed
```

Supplying a confirmation switch is an assertion by the named reviewer. The
tool validates the record and binds it to the exact reference TIFF SHA-256; it
does not perform or infer the biological review.

## Five-case admission checklist

Before running the v1.0 metric gate, the cohort must contain at least five
distinct accepted acquisitions and collectively include:

- native single- and multichannel inputs;
- native `uint8`, `uint16`, and `float32` acquisitions;
- at least two distinct Z spacings;
- one acceptance record and retained review log for every reference; and
- frozen per-case parameters and source-locked candidate provenance.

Numeric type conversion, channel duplication, multiple crops, or multiple time
points from one acquisition do not create missing acquisition diversity.
