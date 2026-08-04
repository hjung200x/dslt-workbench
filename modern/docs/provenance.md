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
