# Release policy

## Preview artifact

Every preview is a self-contained Windows x64 ZIP with an adjacent lowercase
SHA-256 checksum file. The archive contains:

- `Dslt.App.exe`, `dslt_core.dll`, and the .NET self-contained runtime;
- `BUILD-INFO.json` with the exact source commit, runtime identifier, backend,
  validation level, and preserved legacy baseline;
- the complete GPLv3 text, retained third-party notices, source availability,
  .NET runtime license and notices, plus the complete `modern/docs` release
  documentation set.

`package-preview.ps1` starts from clean publish/package directories, builds and
tests the selected native backend unless `-SkipNativeBuild` is supplied by a CI
job that already did so, publishes the WPF app, writes build metadata, creates
the archive and checksum, and invokes `verify-preview-package.ps1`.
It refuses a dirty Git worktree, a non-preview version, or a preservation tag
that no longer resolves to the recorded upstream baseline.

The verifier rejects:

- a checksum mismatch or mismatched checksum filename;
- absolute or parent-traversal ZIP entries;
- duplicate paths under Windows case-insensitive matching;
- missing or empty required files;
- missing GPLv3 completion markers or upstream/baseline provenance;
- an invalid build manifest;
- a package filename whose version or backend disagrees with its build manifest;
- non-x64 application or native PE binaries;
- a framework-dependent package missing self-contained runtime files.

GitHub Actions uploads an artifact only after native tests, ABI integration,
AddressSanitizer stress tests, package construction, and package verification
all succeed.

## v1.0 gate

A verified preview is not sufficient for v1.0. Approval additionally requires:

- representative real datasets in all five required coverage categories;
- a passing `Dslt.Validation` report whose manifest, inputs, references,
  candidates, and provenance sidecars are SHA-256 locked;
- Dice at least 0.995, equal object count, total segmented-volume difference at
  most 0.5%, and 95% Hausdorff distance at most one voxel;
- completed CPU/CUDA parity for every exposed operation on a registered NVIDIA
  runner;
- Windows 10 22H2 and Windows 11 interactive DPI, accessibility, cancellation,
  recovery, and large-volume checks.

The repeatable host portion is collected with `test-windows-host.ps1` according
to `windows-host-validation.md`. v1.0 evidence includes the Windows 10 150% JSON,
the Windows 11 200% JSON, and their paired manual observations.

### Machine-readable final decision

Build the exact CUDA release candidate with
`package-preview.ps1 -Version 1.0.0 -Cuda -ReleaseCandidate`. The candidate is
not approved merely because packaging succeeds.

`verify-v1-release-evidence.ps1` combines the candidate ZIP, source-locked
real-data report, complete 26-operation CUDA parity evidence, both supported Windows host
records, and both structured manual-observation records. It also rejects any
`scaffolded` or `pending-reference` row in the packaged compatibility matrix.
Only its SHA-256-locked report with `passed: true` is v1.0 approval evidence.
See `v1-release-evidence.md`.
