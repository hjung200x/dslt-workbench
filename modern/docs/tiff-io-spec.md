# TIFF, ImageJ, LSM, and label I/O contract

## Validation status

This contract distinguishes decoded-sample preservation from complete file
preservation. Workbench currently has synthetic fixtures for classic TIFF,
ImageJ hyperstack metadata, and signed label TIFF. It does not yet have a real
Zeiss LSM fixture or a legacy-executable capture.

| Capability | Current level |
|---|---|
| Gray8, Gray16, Gray32Float, signed Int32, and unsigned Int32 TIFF pixels | Synthetic-data validated for WIC types and raw Int32 strips using uncompressed, LZW, Deflate, Adobe Deflate, and PackBits encodings |
| ImageJ `C x Z` hyperstack order and calibration | Synthetic-data validated |
| Signed 16-bit legacy label TIFF | Synthetic-data validated |
| Signed 32-bit extended label TIFF | Synthetic-data validated |
| LSM pixels through the Windows TIFF codec | Synthetic-data validated; real fixture required |
| Zeiss LSM core dimensions, voxel sizes, channel names/colors, and timestamps | Synthetic-data validated from tag 34412; real fixture required |

No entry in this table implies legacy comparison or functional equivalence.

## Image volume model

`VolumeData` stores two representations when a TIFF/LSM file is loaded:

- `Samples`: channel-planar float values used by the processing core;
- `VolumeSourceInfo.ChannelPlanarRawSamples`: decoded sample bytes before
  normalization, together with the source voxel type, container, and ImageJ
  description.

The internal order is `C -> Z -> Y -> X`: all voxels for channel 0 are
contiguous, followed by all voxels for channel 1. This matches the native core's
selected-channel contract. ImageJ directories arrive in `XYCZT` order, where C
changes fastest. The loader rejects time series with `frames != 1` rather than
silently flattening T into Z.

All dimension and byte-count products use checked arithmetic. A directory with
a different X/Y size, a metadata/page-count conflict, a non-finite float, or an
unsupported sample format fails before the caller replaces the active volume.
Before managed buffers are allocated, the loader estimates the float working
array, decoded raw array, and one page buffer and rejects a request exceeding
75% of the runtime's reported available memory.

## Supported image TIFF inputs

| TIFF fields | Behavior |
|---|---|
| Classic TIFF magic 42 | Supported |
| BigTIFF magic 43 | Rejected with an explicit error |
| SamplesPerPixel 1 | Supported |
| Photometric MinIsWhite/MinIsBlack | Supported through the Windows Imaging Component decode path |
| Unsigned 8-bit | Decoded bytes preserved; processing floats normalized per channel |
| Unsigned 16-bit | Decoded bytes preserved; processing floats normalized per channel |
| Signed 16-bit | Accepted only when WIC exposes unconverted Gray16 samples |
| IEEE float 32-bit | Decoded IEEE bytes preserved; finite values normalized by maximum absolute value per channel |
| Signed/unsigned image integer 32-bit | Checked raw strips support uncompressed, LZW, Deflate (8/32946), and PackBits compression plus horizontal predictor 2; little- or big-endian samples are canonicalized to bit-exact little-endian decoded bytes and normalized per channel |
| WIC-supported strip compression | Decoded by the installed Windows TIFF codec |

The original loader calls `getXYZStackFloat(..., normalize=true, channel)` and
loads each channel into a separate contiguous region
(`3DFilter/filter3d.cpp:332-338`, `MultiTiffIO/tiff_decorder.cpp:2545-2601`).
Workbench therefore normalizes each channel independently. It keeps decoded raw
samples alongside the processing float array so export and provenance do not
depend on the normalized values.

The `InputSha256` provenance field hashes decoded raw samples when present and
falls back to float samples for synthetic volumes. It is a decoded-volume hash,
not a hash of the container file.

The raw Int32 path accepts one or more strips per page, requires top-left
orientation and chunky grayscale layout, validates every strip range and exact
row byte count, rejects IFD cycles, skips reduced-resolution IFDs, and checks
cancellation between directories and strips. MinIsWhite samples are inverted
over the complete signed or unsigned 32-bit range before canonical storage.
Compressed Int32 strips are decoded without converting their sample type. LZW
uses TIFF EarlyChange=1 with checked 9/10/11/12-bit dictionary transitions;
Deflate accepts zlib-wrapped and raw streams for compression IDs 8 and 32946;
PackBits bounds every literal and repeated run. Predictor 2 is reversed per row
with modulo-32-bit arithmetic before endian canonicalization. Every decoder
requires the exact declared uncompressed byte count and rejects missing LZW EOI,
trailing PackBits data, truncated streams, unsupported compression/predictor,
and decompression overflow. Memory is checked before compressed, decoded, raw,
and float buffers coexist.

## ImageJ metadata

The first IFD's `ImageDescription` is parsed as case-insensitive `key=value`
lines. The following fields affect the volume contract:

| Field | Effect |
|---|---|
| `channels` | Channel count; default 1 |
| `slices` | Z count; defaults to directory count divided by channels |
| `frames` | Must be 1 |
| `pixel_width` / `pixel_height` | X/Y spacing when positive and finite |
| `spacing` | Z spacing when positive and finite |
| `unit` | Calibration unit name; default `pixel` |

When explicit X/Y fields are absent, the inverse TIFF XResolution/YResolution
is used. Metadata declarations must agree exactly with the number of decoded
directories.

## LSM handling

`.lsm` files and TIFFs containing private tag 34412 are identified as `LSM`.
The loader validates the two known `CZ_LSMINFO` magic values, declared structure
size, positive X/Y/Z/C/T dimensions, and finite positive voxel sizes. The core
layout fields are read at their source-compatible offsets: dimensions at bytes
8 through 24 and three IEEE Float64 voxel sizes at bytes 40 through 63. LSM is
required to be little-endian.

`DimensionX` and `DimensionY` must match the full-resolution TIFF frame. The
number of non-thumbnail IFDs must equal `Z x C x T`, and time dimension must be
one. LSM pages are converted from C-fastest Z/C order to internal channel-planar
storage. IFDs whose NewSubfileType marks reduced resolution are excluded;
because Windows codecs differ, the loader accepts either all IFDs or only the
already-filtered full-resolution frames from WIC, and rejects every other frame
count.

Voxel sizes are stored by LSM in meters and converted to `um` calibration.
When present, `OffsetChannelColors` and `OffsetTimeStamps` are resolved as
checked absolute classic-TIFF offsets. The channel block preserves every
length-prefixed UTF-8/Latin-1 fallback name and RGBA display color; the
timestamp block preserves finite, nonnegative, nondecreasing Float64 seconds.
Declared block sizes, relative offsets, channel counts, string lengths, and
timestamp counts are validated before allocation or reading. Missing optional
blocks produce empty metadata arrays.

Malformed magic, size, dimensions, voxel sizes, optional block bounds, channel
metadata, timestamps, IFD cycles, or page-count disagreement fail before the
active volume is replaced. Spectral metadata, multiple time points, and files
beyond classic TIFF's 32-bit offsets remain outside this preview contract.
Complete claims remain blocked until representative real LSM files are
available.

Public layout evidence is the BSD-licensed
[`tifffile` CZ_LSMINFO definition](https://github.com/cgohlke/tifffile/blob/master/tifffile/tifffile.py#L16345-L16360).
Bio-Formats documents strong LSM pixel and metadata support while noting that
its Zeiss specification copies cannot be redistributed. Workbench therefore
keeps real-file comparison as a required gate rather than treating the
synthetic binary fixture as full compatibility proof.

## Label TIFF contract

`LabelTiffCodec` is independent of WIC and implements the compatibility subset
directly. It reads little- or big-endian classic TIFF and writes little-endian
classic TIFF with:

- one signed integer sample per pixel;
- Photometric MinIsBlack;
- top-left orientation;
- no compression on output;
- one output strip per Z page;
- PageNumber and ImageJ description fields;
- XResolution/YResolution and ImageJ Z spacing/unit;
- signed 16-bit `SampleFormat=2` for legacy-compatible output;
- signed 32-bit `SampleFormat=2` for extended output.

The reader accepts multiple uncompressed strips per page, validates every
offset and byte count against file length, rejects directory cycles, and
requires identical dimensions and sample type across pages.

The export rule is deterministic:

```text
if every label is in [-32768, 32767]: write .labels.i16.tif
otherwise:                           write .labels.i32.tif
```

The signed 32-bit path adds this provenance warning:

```text
Labels exceed the legacy signed 16-bit TIFF range; a signed 32-bit TIFF was written.
```

Result packages continue to include the `.i32.raw` payload and JSON sidecar.
The TIFF is an additional interoperable representation; it does not replace
the lossless internal label array.

## Required next fixtures

- at least one LZW-compressed real TIFF for each supported image sample type;
- ImageJ hyperstacks with two or more channels and real X/Y resolution tags;
- a real `.lsm` with trusted channel count and voxel calibration;
- a legacy-generated signed 16-bit segment TIFF;
- real compressed 32-bit signed and unsigned microscopy TIFFs from independent encoders;
- malformed offsets, strip-count mismatch, truncated file, and allocation-limit
  rejection fixtures.
