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

Workbench result provenance schema 1.8 records `sourceCommit` from the managed
assembly's `SourceCommit` metadata. Release-candidate packaging sets that
metadata from the exact Git commit in `BUILD-INFO.json`. Schema-2 real-data
manifests repeat the same commit as `candidateSourceCommit`; the validator
rejects sidecars from any other build. Development builds without injected
source identity record `unavailable` and cannot satisfy the v1 source-locked
gate.
