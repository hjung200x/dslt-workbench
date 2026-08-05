#!/usr/bin/env python3
"""Independently decode and verify the locked public Zeiss LSM cohort."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any

import numpy as np
import tifffile


def digest(path: Path, algorithm: str) -> str:
    value = hashlib.new(algorithm)
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def canonical_czyx(array: np.ndarray, axes: str) -> np.ndarray:
    current = axes
    result = array
    if len(set(current)) != len(current):
        raise ValueError(f"Repeated tifffile axis in {axes!r}.")
    for axis in tuple(current):
        if axis not in "CZYX":
            index = current.index(axis)
            if result.shape[index] != 1:
                raise ValueError(f"Cannot canonicalize non-singleton {axis} axis from {axes!r}.")
            result = np.squeeze(result, axis=index)
            current = current[:index] + current[index + 1 :]
    for axis in "CZYX":
        if axis not in current:
            result = np.expand_dims(result, axis=0)
            current = axis + current
    result = np.transpose(result, [current.index(axis) for axis in "CZYX"])
    return np.ascontiguousarray(result.astype(result.dtype.newbyteorder("<"), copy=False))


def close(left: float, right: float) -> bool:
    return abs(left - right) <= 1e-12 * max(1.0, abs(left), abs(right))


def require_equal(actual: Any, expected: Any, label: str) -> None:
    if actual != expected:
        raise ValueError(f"{label}: expected {expected!r}, received {actual!r}.")


def inspect_item(root: Path, item: dict[str, Any]) -> dict[str, Any]:
    path = (root / item["relativePath"]).resolve()
    if not path.is_file():
        raise FileNotFoundError(path)
    require_equal(path.stat().st_size, item["size"], "file size")
    require_equal(digest(path, "md5"), item["md5"], "file MD5")
    require_equal(digest(path, "sha256"), item["sha256"], "file SHA-256")

    with tifffile.TiffFile(path) as tiff:
        series = tiff.series[0]
        decoded = series.asarray()
        canonical = canonical_czyx(decoded, series.axes)
        metadata = tiff.lsm_metadata
        if metadata is None:
            raise ValueError(f"Missing CZ_LSMINFO metadata in {path.name}.")

    require_equal(series.axes, item["tifffileAxes"], "tifffile axes")
    require_equal(list(decoded.shape), item["tifffileShape"], "tifffile shape")
    require_equal(list(canonical.shape), item["canonicalShapeCzyx"], "canonical CZYX shape")
    require_equal(str(canonical.dtype), item["voxelType"], "voxel type")
    decoded_hash = hashlib.sha256(canonical.tobytes(order="C")).hexdigest()
    require_equal(decoded_hash, item["decodedSha256"], "decoded pixel SHA-256")

    calibration = {
        "x": float(metadata["VoxelSizeX"]) * 1e6,
        "y": float(metadata["VoxelSizeY"]) * 1e6,
        "z": float(metadata["VoxelSizeZ"]) * 1e6,
    }
    for axis in "xyz":
        if not close(calibration[axis], float(item["calibrationUm"][axis])):
            raise ValueError(f"calibration {axis}: expected {item['calibrationUm'][axis]}, received {calibration[axis]}.")

    colors = metadata.get("ChannelColors") or {}
    names = colors.get("ColorNames") or []
    rgba = colors.get("Colors") or []
    channel_metadata = [
        {"name": name, "red": int(color[0]), "green": int(color[1]), "blue": int(color[2]), "alpha": int(color[3])}
        for name, color in zip(names, rgba, strict=True)
    ]
    require_equal(channel_metadata, item["channelMetadata"], "channel metadata")
    timestamp_values = metadata.get("TimeStamps")
    timestamps = [] if timestamp_values is None else [float(value) for value in timestamp_values]
    if len(timestamps) != len(item["timeStampsSeconds"]) or any(
        not close(actual, float(expected)) for actual, expected in zip(timestamps, item["timeStampsSeconds"], strict=True)
    ):
        raise ValueError(f"timestamps: expected {item['timeStampsSeconds']}, received {timestamps}.")

    return {
        "caseId": item["caseId"],
        "path": str(path),
        "tifffileAxes": series.axes,
        "canonicalShapeCzyx": list(canonical.shape),
        "voxelType": str(canonical.dtype),
        "decodedSha256": decoded_hash,
        "calibrationUm": calibration,
        "channelMetadata": channel_metadata,
        "timeStampsSeconds": timestamps,
        "passed": True,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--lock", required=True, type=Path)
    parser.add_argument("--data-directory", required=True, type=Path)
    args = parser.parse_args()
    lock = json.loads(args.lock.read_text(encoding="utf-8"))
    require_equal(lock.get("schemaVersion"), 1, "lock schemaVersion")
    require_equal(tifffile.__version__, lock["independentDecoder"]["version"], "tifffile version")
    results = [inspect_item(args.data_directory, item) for item in lock["items"]]
    print(json.dumps({"schemaVersion": 1, "decoder": f"tifffile {tifffile.__version__}", "passed": True, "items": results}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
