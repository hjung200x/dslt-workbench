# Real-data cohort source audit

This audit separates data that can exercise the validation pipeline from data
that can approve DSLT Workbench v1.0. No dataset is representative merely
because it is public or has expert labels.

## Priority 1: original DSLT leaf acquisitions

The legacy root `README.md` names two Arabidopsis leaf acquisitions in TIFF and
LSM form:

- `ST-ami 1st adaxial 2-1`
- `CS60000 cauline abaxial 2-3`

These are the strongest domain-match candidates because they were distributed
with the original DSLT application and relate directly to the published leaf
workflow ([paper DOI](https://doi.org/10.1111/tpj.12738)). The historical
download host is no longer resolvable as of 2026-08-04. A recovered copy must be
hashed and its provenance recorded before use. TIFF and LSM versions of the
same acquisition count once, so these files alone cannot meet the five-unique-
acquisition release rule. The source list also provides no accepted label
output; legacy output or expert review is still required.

## Priority 2: public pipeline preflight

The [Broad Bioimage Benchmark Collection](https://bbbc.broadinstitute.org/)
provides real microscopy data and ground truth with explicit licensing. These
sets can test acquisition, normalization, label import, metrics, and failure
reporting, but they are not substitutes for representative leaf stacks.

| Source | Useful coverage | Ground truth | License | Release role |
|---|---|---|---|---|
| [BBBC010](https://bbbc.broadinstitute.org/BBBC010) | two-channel, 16-bit, 2D real microscopy | human-corrected binary masks | CC0 | small import/metric preflight |
| [BBBC032](https://bbbc.broadinstitute.org/BBBC032) | four-channel, 3D, 0.5 um Z spacing | manually segmented nuclei | CC0 | large 3D preflight |
| [BBBC033](https://bbbc.broadinstitute.org/BBBC033) | 3D clustered nuclei, 0.5 um Z spacing | manually segmented nuclei | CC0 | medium 3D preflight |
| [BBBC034](https://bbbc.broadinstitute.org/BBBC034) | four-channel, 3D stem cells | manual ground-truth coordinates | CC BY 4.0 | conversion-path preflight |
| [BBBC050](https://bbbc.broadinstitute.org/BBBC050) | time-series 3D, 1.75/2.0 um Z spacing | manual binary and instance labels | CC BY 3.0 | anisotropic-Z preflight |

Public preflight reports must use wording such as `external-pipeline-preflight`
and must not set `dataClassification: representative-real` unless the curator
can justify that classification for the intended DSLT leaf use case.

## Remaining v1.0 acquisition gate

Before v1.0 approval, provide at least five distinct representative leaf
acquisitions covering the schema-2 channel, voxel-type, and Z-spacing matrix.
Each case also needs either:

1. an output produced by the pinned legacy DSLT build with recorded parameters,
   or
2. an expert-reviewed label volume with reviewer and protocol records.

`Dslt.Validation.Prepare` can normalize external masks into the signed TIFF
interchange format, but normalization does not confer biological validity.
