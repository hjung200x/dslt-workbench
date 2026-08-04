# Cortex real-data preflight

## Decision

The five private `Cortex/Col-0/stg1` CZI acquisitions are suitable for
Workbench real-volume input, performance, and qualitative segmentation
preflight. They are not yet sufficient to approve v1.0 because there are no
accepted full-volume 3D reference labels and the five acquisitions do not cover
the required native voxel-type, channel-count, and Z-spacing diversity.

The committed lock at
[`../validation/cortex-stg1-cohort.lock.json`](../validation/cortex-stg1-cohort.lock.json)
contains filenames, source-container hashes, canonical decoded-pixel hashes,
converted TIFF hashes, dimensions, channel names, calibration, and measured
CW2MR saturation. It also pins the conversion dependency versions needed to
reproduce the exact container bytes. It does not contain the private images or
an absolute local path.

## Observed acquisitions

All files contain one non-pyramidal, uncompressed scene with channel axes
`CZYX`, three Gray8 channels (`CW2MR`, `mCher`, and `T-PMT`), XY spacing
`0.311962890625 um`, and Z spacing `0.46 um`.

| Case | Dimensions X x Y x Z x C | CW2MR 99th percentile | CW2MR saturated |
|---|---:|---:|---:|
| Sample 1 | 1024 x 1024 x 74 x 3 | 255 | 12.2970% |
| Sample 2 | 1024 x 1024 x 28 x 3 | 255 | 4.1622% |
| Sample 3 | 1024 x 1024 x 39 x 3 | 211 | 0.0048% |
| Sample 4 | 1024 x 1024 x 33 x 3 | 140 | 0.0001% |
| Sample 5 | 1024 x 1024 x 35 x 3 | 161 | 0.0740% |

CW2MR visibly resolves the cortex cell-wall network in central slices and
maximum-intensity projections. Samples 3 through 5 have the cleanest dynamic
range. Sample 1 remains useful as a bright/saturated stress case, but it should
not be the first parameter-tuning specimen.

## Reproducible conversion

Install the optional preparation dependencies in an isolated Python
environment, then run:

```powershell
python -m pip install -r modern/scripts/requirements-czi.txt
python modern/scripts/convert-czi-cohort.py `
  --source D:\private\Cortex\Col-0\stg1 `
  --output modern\validation\data\cortex-stg1
```

The optional dependency lock records the exact environment used for the
committed TIFF hashes. The converter accepts `uint8`, `uint16`, and `float32`
single-scene CZI volumes whose axes can be reduced to `CZYX`. It rejects time series, mosaics,
multiple scenes, unsupported sample axes, missing calibration, unsupported
types, output-name collisions, and existing output unless `--force` is
explicit. Output is an uncompressed ImageJ HyperStack with Z/C page ordering,
JSON-validated channel names, and micrometer calibration. Workbench exposes
those names as white RGBA channel metadata because ImageJ does not provide a
portable color value in the description contract.

Every conversion is read back before the temporary file is atomically
published. Shape, dtype, every sample, and canonical channel-planar CZYX
SHA-256 must match the decoded CZI. The generated `conversion-audit.json` is
local and ignored by Git.

## Workbench loader and resource preflight

All five converted TIFFs were loaded through the same
`WpfWorkspaceFileService.ReadStack` path as the application and passed the
native DSLT work estimate with the v1.11 manual defaults. Estimate JSON records
the loader-observed `UnsignedInt8`/`TIFF` identity, selected channel,
calibration, channel metadata, and canonical decoded-input SHA-256 in addition
to geometry and resource requirements:

| Case | Host estimate | Directional work items | Gate |
|---|---:|---:|---:|
| Sample 1 | 3.47 GiB | 1,407,876,857,856 | pass |
| Sample 2 | 1.31 GiB | 532,710,162,432 | pass |
| Sample 3 | 1.83 GiB | 741,989,154,816 | pass |
| Sample 4 | 1.55 GiB | 627,836,977,152 | pass |
| Sample 5 | 1.64 GiB | 665,887,703,040 | pass |

These estimates make full-resolution parameter exploration expensive even on
an RTX 4060. Use a physically aligned exploratory crop to choose parameters,
freeze them, and then execute the untouched full acquisition for formal
evidence. A tuning crop is never a release-gate substitute.

### Exploratory Sample 5 CUDA run

A central `512 x 512 x 35` crop of Sample 5 was run on the RTX 4060 with the
v1.11 manual DSLT defaults and no preprocessing. The seven-pass directional
sweep completed without a CUDA or allocation error in approximately 27 minutes
42 seconds. It produced 531 seed components and a signed 16-bit label TIFF.

| Evidence | Value |
|---|---|
| Crop input TIFF SHA-256 | `902875f7a9e1713198134e43fd5c51bff6220913059e01b3ae7321f7b74ad921` |
| Canonical decoded input SHA-256 | `3062e079ecebebef2d9a15ed5ca7f9703cab110ec223555347c493ffd79dc84d` |
| Seed label payload SHA-256 | `db25c49a8a4d52120aea9cfb5d7f33f0d3687ab663d9f3b32c00365387fdc244` |
| Seed label TIFF SHA-256 | `6bb934b072688e8f4a578821fd0b679eaafc6141b4296800f31aeb22f9efa9a5` |
| Labelled voxels | 22,399 of 9,175,040 (0.2441%) |
| Seed volume | minimum 16, median 33, maximum 312 voxels |

The sparse colored regions are DSLT inner-structure seeds, not finished cell
volumes. A Watershed composition is therefore required before qualitative cell
boundary review or metric comparison. The exploratory binary reported
`sourceCommit: unavailable`, so these hashes are performance/preflight evidence
only and cannot enter the source-locked v1.0 manifest.

### Exploratory Watershed composition

The same crop was rerun with all 531 DSLT components selected as Watershed
markers. The complete DSLT-plus-Watershed run took 28 minutes 3 seconds on the
RTX 4060, completed all 256 Watershed levels, and reported no CUDA error.

| Evidence | Value |
|---|---|
| Watershed label payload SHA-256 | `acfa59eafecd08ad1ea474e849d275e6eb785bf37de97988e829b782a9201258` |
| Watershed label TIFF SHA-256 | `797cc6de637b79f2e7e6e2d212bf08107ab8e44b4fba1e0794601d10be59d689` |
| Component count | 531 contiguous labels |
| Labelled voxels | 9,147,793 of 9,175,040 (99.7030%) |
| Component volume | minimum 57, median 3,452, maximum 3,431,173 voxels |

This output is technically complete but is not an acceptable segmentation.
The two largest labels occupy 52.8702% of the crop, merge visibly distinct
central cells, and coexist with many small peripheral regions that do not
follow the cell-wall network. Therefore the manual v1.11 defaults are rejected
for this cohort. The result is useful negative preflight evidence: the CUDA
pipeline runs to completion, but parameters and likely preprocessing/crop
constraints must be tuned on a curator-reviewed training crop before any
full-resolution candidate is generated. No metric is claimed without a 3D
reference label.

## What is still required

The current evidence proves five distinct real volumes can be converted without
pixel loss and loaded with correct dimensions, channels, and calibration. It
does not prove segmentation equivalence. Formal v1.0 admission still requires:

1. curator confirmation that these acquisitions represent the intended DSLT
   biological workflow and that their custody permits local validation;
2. one accepted full-volume legacy output or expert-reviewed 3D instance-label
   TIFF for each admitted acquisition, including review protocol and reviewer;
3. frozen per-case parameters and source-locked Workbench candidate provenance;
4. Dice at least `0.995`, equal object count, segmented-volume difference at
   most `0.5%`, and HD95 at most `1 voxel` for every case; and
5. additional representative native acquisitions covering single-channel,
   `uint16`, `float32`, and a second Z spacing. Numeric conversions of these
   uint8 files can exercise code paths but do not create native acquisition
   diversity.

Until those items exist, the lock deliberately remains
`releaseEligible: false` and the formal metric gate remains zero of five cases.
