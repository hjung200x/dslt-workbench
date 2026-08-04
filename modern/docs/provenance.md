# Provenance

- Workbench repository: <https://github.com/hjung200x/dslt-workbench>
- Upstream: <https://github.com/takashi310/DSLT>
- Preserved commit: `aae2b3e5310fcaad4151a878ad65ed2a3fa29146`
- Local baseline tag: `legacy-baseline-aae2b3e`
- Original copyright notice: Copyright (C) 2014 Kyoto University
- License: GNU General Public License version 3 or later

The legacy files at the repository root are retained as the behavioral and
algorithmic reference. DSLT Workbench implementation files are confined to
`modern/`.

Workbench result provenance schema 1.10 records `sourceCommit` from the managed
assembly's `SourceCommit` metadata. Release-candidate packaging sets that
metadata from the exact Git commit in `BUILD-INFO.json`. Schema-2 real-data
manifests repeat the same commit as `candidateSourceCommit`; the validator
rejects sidecars from any other build. Development builds without injected
source identity record `unavailable` and cannot satisfy the v1 source-locked
gate.

Schema 1.10 also carries `inputSelectedChannel`, which identifies the zero-based
channel actually processed and is validated against the source channel count.
`inputCalibration` records the loaded source spacing while `calibration` records
the exported result spacing. They differ after Z resampling, and the result
calibration is also written into exported label TIFF metadata.
It also carries `inputChannelMetadata`
(channel name and RGBA display color) and `inputTimeStampsSeconds` when the
source container provides them. Empty arrays preserve compatibility for TIFFs
and synthetic volumes without those metadata blocks. The always-present
`processingSteps` array records each operation before the final operation,
including its parameters, actual CPU/CUDA backend, output kind and dimensions,
and canonical output SHA-256. An empty array means no prior processing was
applied. A final Watershed candidate is accepted only when the last prior step
is a hashed `DsltSegmentation` label result and its hash matches the Watershed
seed hash. The source input or last prior step must also have the same
dimensions as the final DSLT/Watershed result; a broken processing-chain
adjacency is rejected before metric evaluation.

`operation.depthColorEnabled` and `operation.depthColorRange` record the
optional Z-projection RGB presentation. The hashed/exported Float32 output stays
the scalar projection; these fields make its display reproducible without
changing the quantitative payload.

`ImportLabels` is a managed workflow operation, not a C ABI v1 processing
operation. Its first ordered history entry records the SHA-256 of decoded label
samples before legacy background/ID normalization. The ordinary `inputSha256`
continues to identify the working image volume, while `outputSha256` identifies
the current normalized or edited label result. A label TIFF calibration mismatch
is reported but does not replace the working-volume calibration, matching the
legacy loader's dimension-only compatibility rule.

`ImportHeightMap` is likewise managed-only. A decoded legacy `.hmp` map records
its canonical Float32 SHA-256, and any later height-relative crop or Z-gradient
operation, surface-area calculation, DepthMap, or HeightProjection records that
active surface hash in ordered history. The transient Float32 surface array is
removed from both `operation.cropHeightMap` and `operation.heightSurface`
before JSON serialization. The corresponding use flag and SHA-256 remain, so
large unversioned arrays are omitted without losing the selected surface's
identity or operation semantics.

`HeightSurfaceArea` is also managed-only. Its result sidecar identifies the
operation while the first ordered history entry records the complete SHA-256 of
the active Float32 height surface and the fixed `10x10` Simpson integration
resolution. The quantitative output hash covers the unnormalized Float32 area
scale factors; the Gray8 TIFF is a presentation preview only.
