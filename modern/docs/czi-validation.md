# CZI validation status

## Automated fixtures

The `dslt_czi_tests` native test generates small CZI documents through the
pinned libCZI writer and reads them through the public `dslt_czi_v1` ABI. It
checks bit-exact `C -> Z -> Y -> X` output for Gray8, Gray16, and Gray32Float,
channel names, X/Y/Z calibration, cancellation, allocation refusal, missing
planes, a non-unit H dimension, mosaic input, malformed input, and handle
lifecycle behavior.

`Dslt.App.Tests` verifies the managed `SafeHandle` layer, provenance transfer,
open progress and cancellation, and preservation of the previous workspace
when loading is cancelled. Tests are opt-in for private files through the
`DSLT_CZI_SMOKE` and `DSLT_CZI_REPEAT_COUNT` environment variables; no private
path or acquisition is required by public CI.

## Cortex cross-reader result

On 2026-08-05, all five private Cortex acquisitions were decoded independently
with GuardVision's Python `czifile` path and DSLT Workbench's pinned libCZI
path. For the `CW2MR` channel, all five matched exactly on:

- C/Z/Y/X geometry and unsigned 8-bit sample type;
- channel identity;
- X/Y spacing of 0.311962890625 micrometres and Z spacing of 0.46
  micrometres;
- SHA-256 of every canonical raw voxel byte.

The observed depths were 74, 28, 39, 33, and 35 slices. Ten complete imports
of every acquisition reproduced the same voxel hash on every run. After each
import was isolated beyond all managed and native handle scopes, settled
private-byte change from first to tenth import was non-positive or below one
megabyte for every acquisition; no persistent per-import growth was observed.

This evidence validates CZI decoding for this five-file Cortex cohort. It does
not establish legacy DSLT segmentation equivalence or satisfy the separate v1.0
expert-reference gate.
