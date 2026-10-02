"""Shared image, grid, and similarity primitives for the skill library tools."""

from __future__ import annotations

import json
import math
from pathlib import Path
from typing import Any, Iterable

import numpy as np
from PIL import Image


CANONICAL_SIZE = (520, 142)
GRID_COLUMNS = 14
GRID_X_START = 5
GRID_X_STEP = 36
GRID_SLOT_SIZE = 32
GRID_ROW_Y = (22, 58, 104)
BOTTOM_KEYS = ("Q", "W", "E", "R", "A", "S", "D", "Z", "X", "C", "", "", "", "")


def load_catalog(path: Path) -> list[dict[str, Any]]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def write_catalog(path: Path, records: Iterable[dict[str, Any]]) -> None:
    value = "".join(json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n" for record in records)
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(value, encoding="utf-8")
    temp.replace(path)


def parse_bbox(value: str) -> tuple[int, int, int, int]:
    parts = tuple(round(float(part.strip())) for part in value.split(","))
    if len(parts) != 4 or parts[2] <= parts[0] or parts[3] <= parts[1]:
        raise ValueError("bbox must be x1,y1,x2,y2")
    return parts


def load_skill_crop(path: Path, bbox: tuple[int, int, int, int] | None) -> Image.Image:
    image = Image.open(path).convert("RGB")
    if bbox is not None:
        image = image.crop(bbox)
    if image.size != CANONICAL_SIZE:
        image = image.resize(CANONICAL_SIZE, Image.Resampling.LANCZOS)
    return image


def extract_slots(crop: Image.Image) -> list[dict[str, Any]]:
    slots: list[dict[str, Any]] = []
    for row, y in enumerate(GRID_ROW_Y):
        for column in range(GRID_COLUMNS):
            x = GRID_X_START + column * GRID_X_STEP
            box = (x, y, x + GRID_SLOT_SIZE, y + GRID_SLOT_SIZE)
            key = BOTTOM_KEYS[column] if row == 2 else ""
            slots.append(
                {
                    "slot_id": f"r{row + 1}c{column + 1}",
                    "row": row,
                    "column": column,
                    "bbox": list(box),
                    "key_hint": key,
                    "image": crop.crop(box),
                }
            )
    return slots


def inner_array(image: Image.Image, size: int = 32, border: int = 3, mask_key_hint: bool = False) -> np.ndarray:
    rgb = np.asarray(image.convert("RGB").resize((size, size), Image.Resampling.LANCZOS), dtype=np.float32)
    inner = rgb[border : size - border, border : size - border].copy()
    if mask_key_hint:
        inner[0:11, 18:26] = 0
    return inner


def grayscale(rgb: np.ndarray) -> np.ndarray:
    return rgb[:, :, 0] * 0.299 + rgb[:, :, 1] * 0.587 + rgb[:, :, 2] * 0.114


def normalized_correlation(left: np.ndarray, right: np.ndarray) -> float:
    if left.shape != right.shape:
        raise ValueError("NCC inputs must have the same shape")
    a = left.astype(np.float32) - float(left.mean())
    b = right.astype(np.float32) - float(right.mean())
    denominator = math.sqrt(float((a * a).sum()) * float((b * b).sum()))
    return float((a * b).sum() / denominator) if denominator > 1e-6 else 0.0


def color_histogram(rgb: np.ndarray, bins: int = 8) -> np.ndarray:
    values = []
    for channel in range(3):
        hist, _ = np.histogram(rgb[:, :, channel], bins=bins, range=(0, 256))
        values.extend(hist.astype(np.float32))
    output = np.asarray(values, dtype=np.float32)
    norm = float(np.linalg.norm(output))
    return output / norm if norm > 1e-6 else output


def histogram_similarity(left: np.ndarray, right: np.ndarray) -> float:
    denominator = float(np.linalg.norm(left) * np.linalg.norm(right))
    return float(np.dot(left, right) / denominator) if denominator > 1e-6 else 0.0


def difference_hash(rgb: np.ndarray) -> int:
    image = Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8), "RGB").convert("L").resize((9, 8), Image.Resampling.LANCZOS)
    values = np.asarray(image, dtype=np.int16)
    bits = values[:, 1:] >= values[:, :-1]
    result = 0
    for bit in bits.flat:
        result = (result << 1) | int(bit)
    return result


def hamming_distance(left: int, right: int) -> int:
    return (left ^ right).bit_count()


def visual_features(image: Image.Image, mask_key_hint: bool = False) -> dict[str, Any]:
    rgb = inner_array(image, mask_key_hint=mask_key_hint)
    return {
        "rgb": rgb,
        "gray": grayscale(rgb),
        "hist": color_histogram(rgb),
        "dhash": difference_hash(rgb),
    }


def identity_score(left: dict[str, Any], right: dict[str, Any]) -> float:
    rgb = normalized_correlation(left["rgb"], right["rgb"])
    gray = normalized_correlation(left["gray"], right["gray"])
    hist = histogram_similarity(left["hist"], right["hist"])
    # Negative NCC is not evidence for identity; the histogram is only a weak tie-breaker.
    return max(0.0, rgb) * 0.64 + max(0.0, gray) * 0.26 + max(0.0, hist) * 0.10


def empty_score(image: Image.Image) -> float:
    rgb = inner_array(image, mask_key_hint=False)
    gray = grayscale(rgb)
    saturation = rgb.max(axis=2) - rgb.min(axis=2)
    edge = (np.abs(np.diff(gray, axis=0)).mean() + np.abs(np.diff(gray, axis=1)).mean()) / 2.0
    if float(gray.std()) < 7.0 or edge < 1.8:
        return 0.98
    visual = min(1.0, float(saturation.mean()) / 38.0) * 0.45 + min(1.0, float(edge) / 25.0) * 0.55
    return max(0.0, min(1.0, 1.0 - visual))


def cooldown_features(image: Image.Image) -> dict[str, Any]:
    rgb = np.asarray(image.convert("RGB"), dtype=np.int16)
    channel_max = rgb.max(axis=2)
    channel_min = rgb.min(axis=2)
    gray_ratio = float(((channel_max - channel_min < 38) & (channel_max < 175)).mean())
    cyan_digit = (rgb[:, :, 0] < 80) & (rgb[:, :, 1] > 125) & (rgb[:, :, 2] > 155)
    digit_pixels = int(cyan_digit[4:28, 4:28].sum())
    if gray_ratio >= 0.62 or 18 <= digit_pixels <= 110:
        state = "cooling"
    elif gray_ratio < 0.56 and (digit_pixels < 18 or digit_pixels > 150):
        state = "ready"
    else:
        state = "unknown"
    return {"state": state, "gray_ratio": round(gray_ratio, 4), "digit_pixels": digit_pixels}


def feature_record(image: Image.Image) -> dict[str, Any]:
    feature = visual_features(image, mask_key_hint=False)
    masked = visual_features(image, mask_key_hint=True)
    return {
        "dhash": f"{feature['dhash']:016x}",
        "hist": [round(float(value), 7) for value in feature["hist"]],
        "dhash_masked": f"{masked['dhash']:016x}",
        "hist_masked": [round(float(value), 7) for value in masked["hist"]],
    }
