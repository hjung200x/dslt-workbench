# Public Zeiss LSM interoperability evidence

This evidence isolates file-format validation from biological segmentation
validation. Three public CC BY 4.0 Zeiss LSM files are decoded independently by
`tifffile 2026.5.15` and by the Workbench loader. Both decoders must produce the
same little-endian channel-planar `CZYX` pixel SHA-256, dimensions, voxel type,
micrometer calibration, channel names/colors, and timestamps.

## Locked cases

| Case | Source layout | Canonical CZYX | Type | Exact decoded result |
|---|---|---:|---|---|
| Zenodo 5781661 | one full-resolution grayscale IFD plus thumbnail | `1 x 1 x 512 x 512` | uint16 | pass |
| Zenodo 4633301 | three planar samples in one full-resolution IFD plus thumbnail | `3 x 1 x 512 x 512` | uint8 | pass |
| Zenodo 3594412 | 57 full-resolution IFDs, two planar samples per IFD, interleaved thumbnails | `2 x 57 x 1024 x 1024` | uint8 | pass |

The exact file and decoded hashes, Zenodo record attribution, calibration,
channel metadata, and timestamps are pinned in
[`public-lsm-interoperability.lock.json`](../validation/public-lsm-interoperability.lock.json).
The binary files remain ignored and are downloaded in place.

## Reproduce without administrator access

From the repository root in PowerShell:

```powershell
python -m venv modern/validation/data/python-czi-env
modern/validation/data/python-czi-env/Scripts/python.exe -m pip install `
  -r modern/scripts/requirements-czi.txt
./modern/scripts/fetch-public-lsm-cohort.ps1
./modern/scripts/verify-public-lsm-interoperability.ps1
```

The last command first runs the independent Python decoder, then calls
`Dslt.Validation.Prepare inspect-volume`, which uses the same loader as the WPF
application. It fails on any file hash, decoded pixel, shape, type, calibration,
channel metadata, timestamp, or independent-decoder mismatch. CI runs the
network-free `-LockOnly` form; the full data comparison is intentionally local
because the largest locked source is about 122 MB.

## Scope boundary

This proves interoperability for the three locked real files and fixes a
previously uncovered layout in which one LSM IFD stores multiple channel planes.
It does not prove equality with the unavailable original DSLT sample LSMs, does
not provide leaf segmentation labels, and contributes zero cases to the five
representative-acquisition v1.0 metric gate.

Sources: [Zenodo 5781661](https://zenodo.org/records/5781661),
[Zenodo 4633301](https://zenodo.org/records/4633301), and
[Zenodo 3594412](https://zenodo.org/records/3594412).
