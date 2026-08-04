# Legacy runtime and parameter-equivalence status

This document prevents source-level compatibility from being reported as
observed legacy-binary equivalence. The recovery evidence is pinned in
[`legacy-distribution.lock.json`](../validation/legacy-distribution.lock.json):
the v1.11 manual and complete source baseline are available, but no installer,
sample acquisition, or executable runtime oracle was recovered.

## Evidence hierarchy

1. The pinned source at `aae2b3e5310fcaad4151a878ad65ed2a3fa29146`
   defines deterministic arithmetic, ordering, sign/scale conversions, and
   callable versus no-op commands.
2. The preserved v1.11 manual defines visible workflow and screenshot values.
3. Synthetic CPU/CUDA/ABI/WPF fixtures validate the Workbench interpretation.
4. Runtime-only ambiguities remain `capture-required`; they are not silently
   resolved from malformed slider declarations or documentation prose.

No legacy installer is installed or executed as part of this evidence. If one
is recovered, it must first be hashed, malware-scanned, and inspected; execution
requires a separate isolated test decision and is not an administrator-level
installation step.

## Parameter decisions

| Area | Locked Workbench meaning | Evidence status |
|---|---|---|
| Adaptive threshold | radius `0..100`; mean/Gaussian; `core C = -0.002 * visible offset`; default visible offset 20 | CPU source and UI mapping fixed; legacy CUDA axis/C divergence remains runtime-required |
| Height map | XY/Z radii `0..64`, Gaussian default, Z radius 4, smooth level 1, strict crossing and clamp boundary | GPU-visible source path is the reference; legacy CPU fallback divergence is recorded |
| DSLT directional sweep | Gaussian, radius 14, level 2, Z correction 0.2; manual visible max/min/interval 10/4/-1 map to core `-0.008/-0.020/+0.002`; validation area 800 and closing 2 | Source/code-behind/manual mapping and fixtures fixed; legacy-binary voxel oracle unavailable |
| H-minima | height `0..1`, default 0.1; check interval `1..10000`, default 50 | Source contract and fixtures fixed; runtime comparison unavailable |
| Height projection depth color | Z/normal scalar rules; depth color only in Z mode; range `1..500`, default 100 | Source formula fixed; unsafe legacy zero endpoint is explicitly rejected |
| Z-gradient correction | source-authoritative height-relative formula; coefficient `0..10` default 10, exponent `1..10` default 1 | Manual simplified prose is not used to replace source arithmetic |
| Watershed | fixed 256 levels, legacy six-neighbor priority, selected seeds and minimum size | hidden stride 0.001 is not exposed because the source never reads it |
| Edit morphology | radius defaults to 1, edit iterations default to 1, erosion edge clamp defaults on in Workbench | source behavior and manual workflow fixed; manual radius 3 is an example, not a universal default |

The complete formulas and per-operation fixtures remain in the focused `*-spec.md`
documents and the compatibility matrix. The WPF default/mapping tests lock the
current values used to construct `OperationParameters`.

## Release consequence

The parameter contract is complete at source/manual/synthetic level, but binary
equivalence is not observable with current evidence. Rows whose only missing
proof is an archived-runtime oracle therefore remain `scaffolded`; this is an
evidence limitation, not permission to weaken or bypass the v1.0 release gate.
The five representative acquisitions and their accepted labels are an
independent remaining gate.
