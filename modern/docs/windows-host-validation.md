# Windows host validation

The automated CI and synthetic fixtures do not replace interactive validation
on the supported Windows releases. `test-windows-host.ps1` turns the repeatable
part of that gate into a machine-readable JSON evidence file plus SHA-256 while
still launching the real self-contained package.

## Required host matrix

Before v1.0, collect evidence from both of these environments:

| Host | Display scaling | Backend expectation |
| --- | ---: | --- |
| Windows 10 22H2 x64 | 150% | CPU fallback is acceptable |
| Windows 11 x64 | 200% | Run once with `-RequireCuda` on the registered NVIDIA host |

Use a 1920 x 1080 or larger primary display. Set the requested Windows display
scaling before launching the script and sign out/in if Windows requests it.
Do not relabel a newer compatibility mode or server build as Windows 10 22H2.

## Run the validator

Extract the verified preview ZIP without changing its contents. From a clean
repository checkout, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\modern\scripts\test-windows-host.ps1 `
  -PackageRoot C:\validation\dslt-workbench `
  -EvidencePath C:\validation\evidence\windows-11-200.json `
  -ExpectedDpiPercent 200 `
  -RequireCuda
```

Use `-ExpectedDpiPercent 150` and omit `-RequireCuda` for the Windows 10 CPU
fallback run. Pass `-Force` only when intentionally replacing an earlier local
evidence file.

The validator checks:

- `BUILD-INFO.json`, self-contained `win-x64`, and application/native hashes;
- the exact Windows product, display version, build, UBR, and architecture;
- main-window startup and normal close;
- current window DPI and optional exact expected scaling;
- `PerMonitorV2` awareness;
- native-core readiness and, when requested, CUDA readiness;
- zero keyboard-focusable UI Automation elements without a name;
- the 900 x 500 responsive minimum;
- UI Automation scrolling of Open, Run, Cancel, and Export into view.

The JSON and adjacent `.sha256` file are evidence for one package, machine, OS
build, and scaling setting. They are not proof of real-data equivalence and do
not themselves approve v1.0.

## Manual observations paired with each JSON

Record these observations beside the evidence file:

1. Open one representative file-backed TIFF/LSM volume.
2. Start a long operation, cancel it, and confirm the source and last valid
   result remain visible.
3. Trigger one recoverable input or processing error and confirm the prior state
   remains intact.
4. Navigate all XY/YZ/ZX views at minimum window size and the configured display
   scaling.
5. Export a result and confirm its TIFF plus JSON provenance sidecar reopen.

The final release audit must include both host JSON files, the paired manual
observations, and the five-case real-data validation report.
