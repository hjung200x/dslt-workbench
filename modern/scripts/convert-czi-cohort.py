#!/usr/bin/env python3
"""Convert simple Zeiss CZI volumes into verified ImageJ HyperStack TIFFs.

This is an optional real-data preparation tool. It intentionally does not make
the Workbench depend on a CZI runtime and it does not classify an acquisition
as release-eligible or create reference labels.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
from pathlib import Path
from typing import Any

try:
    import czifile
    import numpy as np
    import tifffile
except ImportError as exc:  # pragma: no cover - exercised before dependencies exist
    raise SystemExit(
        "CZI conversion requires numpy, czifile, and tifffile. "
        "Install them in an isolated environment with "
        "'python -m pip install numpy czifile tifffile'."
    ) from exc


SUPPORTED_DTYPES = {np.dtype("uint8"), np.dtype("uint16"), np.dtype("float32")}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Convert one CZI or a directory of CZI files to pixel-verified ImageJ TIFFs."
    )
    parser.add_argument("--source", required=True, type=Path, help="CZI file or directory")
    parser.add_argument("--output", required=True, type=Path, help="Output directory")
    parser.add_argument(
        "--recursive", action="store_true", help="Search source directories recursively"
    )
    parser.add_argument(
        "--force", action="store_true", help="Replace existing converted TIFFs and audit JSON"
    )
    return parser.parse_args()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(8 * 1024 * 1024):
            digest.update(block)
    return digest.hexdigest()


def sha256_array(array: np.ndarray[Any, Any]) -> str:
    canonical = np.ascontiguousarray(array)
    return hashlib.sha256(memoryview(canonical)).hexdigest()


def safe_stem(path: Path) -> str:
    value = re.sub(r"[^A-Za-z0-9._-]+", "_", path.stem).strip("._")
    if not value:
        raise ValueError(f"CZI filename does not contain a usable output stem: {path.name}")
    return value


def source_paths(source: Path, recursive: bool) -> list[Path]:
    source = source.resolve()
    if source.is_file():
        if source.suffix.lower() != ".czi":
            raise ValueError(f"Source file must have a .czi extension: {source}")
        return [source]
    if not source.is_dir():
        raise FileNotFoundError(f"CZI source was not found: {source}")
    pattern = "**/*.czi" if recursive else "*.czi"
    paths = sorted(source.glob(pattern), key=lambda value: value.as_posix().lower())
    if not paths:
        raise FileNotFoundError(f"No CZI files were found in: {source}")
    return paths


def as_czyx(array: np.ndarray[Any, Any], axes: str) -> np.ndarray[Any, Any]:
    if len(axes) != array.ndim or len(set(axes)) != len(axes):
        raise ValueError(f"Invalid axis declaration {axes!r} for shape {array.shape}")
    unsupported = set(axes) - set("CZYX")
    if unsupported:
        raise ValueError(
            f"Unsupported CZI axes {axes!r}; split scenes/time/sample axes before conversion"
        )
    if "Y" not in axes or "X" not in axes:
        raise ValueError(f"CZI scene must contain Y and X axes, got {axes!r}")

    expanded = array
    expanded_axes = axes
    for axis in "CZ":
        if axis not in expanded_axes:
            expanded = np.expand_dims(expanded, axis=0)
            expanded_axes = axis + expanded_axes
    order = tuple(expanded_axes.index(axis) for axis in "CZYX")
    return np.ascontiguousarray(np.transpose(expanded, order))


def micrometers(scene: Any, axis: str) -> float:
    try:
        value = float(scene.coord_scales[axis])
        unit = str(scene.coord_units[axis]).strip().lower()
    except (KeyError, TypeError, ValueError) as exc:
        raise ValueError(f"CZI scene has no valid {axis} spacing") from exc
    factors = {
        "m": 1_000_000.0,
        "meter": 1_000_000.0,
        "metre": 1_000_000.0,
        "um": 1.0,
        "µm": 1.0,
        "μm": 1.0,
        "micrometer": 1.0,
        "micrometre": 1.0,
        "nm": 0.001,
        "nanometer": 0.001,
        "nanometre": 0.001,
    }
    if unit not in factors:
        raise ValueError(f"Unsupported CZI {axis} spacing unit: {unit!r}")
    result = value * factors[unit]
    if not np.isfinite(result) or result <= 0:
        raise ValueError(f"CZI {axis} spacing must be positive and finite")
    return result


def channel_names(scene: Any, count: int) -> list[str]:
    names = [str(value).strip() for value in scene.channels]
    if len(names) != count:
        names = []
    return [value or f"Channel {index + 1}" for index, value in enumerate(names)] \
        if names else [f"Channel {index + 1}" for index in range(count)]


def rational_value(value: Any) -> float:
    if isinstance(value, tuple) and len(value) == 2:
        numerator, denominator = value
        return float(numerator) / float(denominator)
    return float(value)


def verify_tiff(
    path: Path,
    expected_czyx: np.ndarray[Any, Any],
    expected_spacing: list[float],
    expected_channels: list[str],
) -> tuple[str, dict[str, Any]]:
    with tifffile.TiffFile(path) as image:
        if not image.is_imagej:
            raise ValueError(f"Converted TIFF is missing ImageJ metadata: {path}")
        series = image.series[0]
        actual_czyx = as_czyx(np.asarray(series.asarray()), series.axes)
        metadata = dict(image.imagej_metadata or {})
        first_page = image.pages[0]
        spacing_x = 1.0 / rational_value(first_page.tags["XResolution"].value)
        spacing_y = 1.0 / rational_value(first_page.tags["YResolution"].value)
    if actual_czyx.dtype != expected_czyx.dtype or actual_czyx.shape != expected_czyx.shape:
        raise ValueError(
            f"Converted TIFF geometry changed: expected {expected_czyx.shape}/{expected_czyx.dtype}, "
            f"got {actual_czyx.shape}/{actual_czyx.dtype}"
        )
    if not np.array_equal(actual_czyx, expected_czyx, equal_nan=True):
        raise ValueError(f"Converted TIFF samples differ from decoded CZI pixels: {path}")
    actual_spacing = [spacing_x, spacing_y, float(metadata.get("spacing", 0))]
    if not np.allclose(actual_spacing, expected_spacing, rtol=1e-9, atol=1e-12):
        raise ValueError(
            f"Converted TIFF calibration changed: expected {expected_spacing}, got {actual_spacing}"
        )
    if str(metadata.get("unit", "")).lower() != "um":
        raise ValueError(f"Converted TIFF micrometer unit is missing: {path}")
    try:
        actual_channels = json.loads(str(metadata.get("channel_names", "")))
    except json.JSONDecodeError as exc:
        raise ValueError(f"Converted TIFF channel metadata is not valid JSON: {path}") from exc
    if actual_channels != expected_channels:
        raise ValueError(
            f"Converted TIFF channel names changed: expected {expected_channels}, got {actual_channels}"
        )
    return sha256_array(actual_czyx), metadata


def convert_one(source: Path, output: Path) -> dict[str, Any]:
    with czifile.CziFile(source) as container:
        if len(container.scenes) != 1:
            raise ValueError(
                f"CZI must contain exactly one scene; split {len(container.scenes)} scenes first: {source}"
            )
        scene = container.scenes[0]
        decoded_czyx = as_czyx(np.asarray(scene.asarray()), scene.axes)
        if decoded_czyx.dtype not in SUPPORTED_DTYPES:
            raise ValueError(
                f"ImageJ conversion supports uint8, uint16, or float32, got {decoded_czyx.dtype}"
            )
        spacing = [micrometers(scene, axis) for axis in "XYZ"]
        channels = channel_names(scene, decoded_czyx.shape[0])

    temporary = output.with_name(output.name + ".part")
    try:
        tifffile.imwrite(
            temporary,
            np.transpose(decoded_czyx, (1, 0, 2, 3)),
            imagej=True,
            resolution=(1.0 / spacing[0], 1.0 / spacing[1]),
            metadata={
                "axes": "ZCYX",
                "spacing": spacing[2],
                "unit": "um",
                "Labels": channels,
                "channel_names": json.dumps(
                    channels, ensure_ascii=False, separators=(",", ":")
                ),
            },
        )
        output_decoded_sha256, imagej_metadata = verify_tiff(
            temporary, decoded_czyx, spacing, channels
        )
        os.replace(temporary, output)
    finally:
        temporary.unlink(missing_ok=True)

    decoded_sha256 = sha256_array(decoded_czyx)
    if output_decoded_sha256 != decoded_sha256:
        raise ValueError(f"Canonical decoded SHA-256 changed during conversion: {source}")
    return {
        "id": safe_stem(source).lower().replace("_", "-"),
        "sourceFile": source.name,
        "sourceFileSha256": sha256_file(source),
        "inputTiff": output.name,
        "inputFileSha256": sha256_file(output),
        "decodedCzyxSha256": decoded_sha256,
        "width": int(decoded_czyx.shape[3]),
        "height": int(decoded_czyx.shape[2]),
        "depth": int(decoded_czyx.shape[1]),
        "channelCount": int(decoded_czyx.shape[0]),
        "channels": channels,
        "voxelType": str(decoded_czyx.dtype),
        "spacingUm": {"x": spacing[0], "y": spacing[1], "z": spacing[2]},
        "imagejImages": int(imagej_metadata.get("images", decoded_czyx.shape[0] * decoded_czyx.shape[1])),
        "pixelExact": True,
    }


def main() -> int:
    args = parse_args()
    paths = source_paths(args.source, args.recursive)
    output_root = args.output.resolve()
    output_root.mkdir(parents=True, exist_ok=True)
    destinations = [output_root / f"{safe_stem(path)}.imagej.tif" for path in paths]
    if len(set(destinations)) != len(destinations):
        raise ValueError("Two CZI inputs resolve to the same sanitized output filename")
    audit_path = output_root / "conversion-audit.json"
    existing = [path for path in [*destinations, audit_path] if path.exists()]
    if existing and not args.force:
        listing = ", ".join(path.name for path in existing)
        raise FileExistsError(f"Output already exists ({listing}); pass --force to replace it")

    cases = []
    for source, destination in zip(paths, destinations, strict=True):
        case = convert_one(source, destination)
        cases.append(case)
        print(
            f"Converted {source.name}: "
            f"{case['width']}x{case['height']}x{case['depth']}x{case['channelCount']} "
            f"{case['voxelType']}"
        )

    audit = {
        "schema": 1,
        "tool": "modern/scripts/convert-czi-cohort.py",
        "dataClassification": "input-preflight-only",
        "releaseEligible": False,
        "dependencies": {
            "numpy": np.__version__,
            "czifile": getattr(czifile, "__version__", "unknown"),
            "tifffile": tifffile.__version__,
        },
        "notes": [
            "CZI originals remain immutable and are not redistributed.",
            "TIFF pages use ImageJ ZCYX metadata with C-fastest page ordering.",
            "Pixel equality and canonical CZYX SHA-256 are verified after conversion.",
            "Conversion does not provide accepted 3D reference labels or curator approval.",
        ],
        "cases": cases,
    }
    temporary_audit = audit_path.with_name(audit_path.name + ".part")
    try:
        temporary_audit.write_text(json.dumps(audit, indent=2) + "\n", encoding="utf-8")
        os.replace(temporary_audit, audit_path)
    finally:
        temporary_audit.unlink(missing_ok=True)
    print(f"Wrote audit: {audit_path}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (FileNotFoundError, ValueError, OSError) as exc:
        print(f"CZI conversion failed: {exc}", file=sys.stderr)
        raise SystemExit(2) from exc
