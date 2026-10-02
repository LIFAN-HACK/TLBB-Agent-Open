from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

import numpy as np
from PIL import Image


ROOT = Path(__file__).resolve().parent
CONFIG_PATH = ROOT / "locator_config.json"


def read_rgb(path: str) -> np.ndarray | None:
    try:
        return np.asarray(Image.open(path).convert("RGB"), dtype=np.uint8)
    except Exception:
        return None


def gray(image: np.ndarray) -> np.ndarray:
    value = image.astype(np.float32)
    return value[:, :, 0] * 0.299 + value[:, :, 1] * 0.587 + value[:, :, 2] * 0.114


def resize_gray(image: np.ndarray, width: int, height: int) -> np.ndarray:
    clipped = np.clip(image, 0, 255).astype(np.uint8)
    return np.asarray(Image.fromarray(clipped, mode="L").resize((width, height), Image.Resampling.LANCZOS), dtype=np.float32)


def next_power_of_two(value: int) -> int:
    return 1 << max(0, value - 1).bit_length()


def window_sum(array: np.ndarray, height: int, width: int) -> np.ndarray:
    # Full-screen squared sums exceed float32's useful integral-image precision.
    # Use float64 so NCC remains bounded by [-1, 1] on large frames.
    integral = np.pad(array.astype(np.float64), ((1, 0), (1, 0)), mode="constant").cumsum(axis=0).cumsum(axis=1)
    return integral[height:, width:] - integral[:-height, width:] - integral[height:, :-width] + integral[:-height, :-width]


def ncc_map(search: np.ndarray, template: np.ndarray) -> np.ndarray:
    sh, sw = search.shape
    th, tw = template.shape
    if th > sh or tw > sw:
        return np.empty((0, 0), dtype=np.float32)
    centered = template.astype(np.float32) - float(template.mean())
    template_energy = float((centered * centered).sum())
    if template_energy <= 1e-6:
        return np.empty((0, 0), dtype=np.float32)
    fft_shape = (next_power_of_two(sh + th - 1), next_power_of_two(sw + tw - 1))
    search_fft = np.fft.rfft2(search.astype(np.float32), fft_shape)
    template_fft = np.fft.rfft2(np.flip(centered, axis=(0, 1)), fft_shape)
    correlation = np.fft.irfft2(search_fft * template_fft, fft_shape)
    numerator = correlation[th - 1 : sh, tw - 1 : sw]
    sums = window_sum(search, th, tw)
    sums2 = window_sum(search * search, th, tw)
    variance = np.maximum(sums2 - (sums * sums) / float(th * tw), 1e-6)
    denominator = np.sqrt(variance * template_energy)
    return (numerator / denominator).astype(np.float32)


def direct_ncc(patch: np.ndarray, template: np.ndarray) -> float:
    if patch.shape != template.shape:
        return -1.0
    left = patch.astype(np.float32) - float(patch.mean())
    right = template.astype(np.float32) - float(template.mean())
    denom = math.sqrt(float((left * left).sum()) * float((right * right).sum()))
    return -1.0 if denom <= 1e-8 else float((left * right).sum() / denom)


def clamp_box(box: list[int], width: int, height: int) -> list[int]:
    x1, y1, x2, y2 = box
    return [max(0, min(width, x1)), max(0, min(height, y1)), max(0, min(width, x2)), max(0, min(height, y2))]


def locate_one(frame_gray: np.ndarray, template_gray: np.ndarray, spec: dict, search_downsample: float) -> dict | None:
    frame_h, frame_w = frame_gray.shape
    rx1, ry1, rx2, ry2 = spec["search_roi_normalized"]
    roi_box = clamp_box([round(rx1 * frame_w), round(ry1 * frame_h), round(rx2 * frame_w), round(ry2 * frame_h)], frame_w, frame_h)
    x1, y1, x2, y2 = roi_box
    roi = frame_gray[y1:y2, x1:x2]
    if roi.size == 0:
        return None
    down_w = max(1, round(roi.shape[1] * search_downsample))
    down_h = max(1, round(roi.shape[0] * search_downsample))
    down_roi = resize_gray(roi, down_w, down_h)
    best: dict | None = None
    for scale in spec.get("scales", [1.0]):
        tw = max(8, round(template_gray.shape[1] * scale * search_downsample))
        th = max(8, round(template_gray.shape[0] * scale * search_downsample))
        tpl = resize_gray(template_gray, tw, th)
        scores = ncc_map(down_roi, tpl)
        if scores.size == 0:
            continue
        flat_index = int(np.nanargmax(scores))
        yy, xx = np.unravel_index(flat_index, scores.shape)
        score = float(scores[yy, xx])
        candidate = {"score": score, "scale": float(scale), "x": x1 + round(xx / search_downsample), "y": y1 + round(yy / search_downsample)}
        if best is None or score > best["score"]:
            best = candidate
    if best is None:
        return None

    scale = best["scale"]
    full_tw = max(2, round(template_gray.shape[1] * scale))
    full_th = max(2, round(template_gray.shape[0] * scale))
    full_template = resize_gray(template_gray, full_tw, full_th)
    refined = dict(best)
    for yy in range(max(y1, best["y"] - 4), min(y2 - full_th, best["y"] + 4) + 1):
        for xx in range(max(x1, best["x"] - 4), min(x2 - full_tw, best["x"] + 4) + 1):
            score = direct_ncc(frame_gray[yy : yy + full_th, xx : xx + full_tw], full_template)
            if score > refined["score"]:
                refined = {"score": score, "scale": scale, "x": xx, "y": yy}

    threshold = float(spec.get("threshold", 0.7))
    if refined["score"] < threshold:
        return None
    offset_x, offset_y = spec.get("template_offset", [0, 0])
    panel_w, panel_h = spec["panel_size"]
    panel_x = round(refined["x"] - offset_x * scale)
    panel_y = round(refined["y"] - offset_y * scale)
    panel_bbox = clamp_box([panel_x, panel_y, panel_x + round(panel_w * scale), panel_y + round(panel_h * scale)], frame_w, frame_h)
    return {
        "confidence": round(float(refined["score"]), 4),
        "scale": round(scale, 4),
        "bbox": panel_bbox,
        "center": [round((panel_bbox[0] + panel_bbox[2]) / 2), round((panel_bbox[1] + panel_bbox[3]) / 2)],
        "match_bbox": [refined["x"], refined["y"], refined["x"] + full_tw, refined["y"] + full_th],
        "search_roi": roi_box,
        "threshold": threshold,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--image", required=True)
    parser.add_argument("--classes", default="", help="Comma-separated class allowlist; empty scans every configured class.")
    args = parser.parse_args()
    frame = read_rgb(args.image)
    if frame is None:
        result = {"ok": False, "error": "cannot read image"}
        sys.stdout.write(json.dumps(result, ensure_ascii=False))
        return 2
    config = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
    allow = {item.strip() for item in args.classes.split(",") if item.strip()}
    frame_gray = gray(frame)
    detections = []
    for class_name, spec in config["classes"].items():
        if allow and class_name not in allow:
            continue
        best = None
        best_template = ""
        for rel_path in spec["templates"]:
            template = read_rgb(str(ROOT / rel_path))
            if template is None:
                continue
            located = locate_one(frame_gray, gray(template), spec, float(config.get("search_downsample", 0.5)))
            if located and (best is None or located["confidence"] > best["confidence"]):
                best = located
                best_template = rel_path
        if best:
            best["class_name"] = class_name
            best["template"] = best_template
            detections.append(best)
    detections.sort(key=lambda item: item["confidence"], reverse=True)
    result = {
        "ok": True,
        "engine": "l1_ui_aux_template_locator_v1",
        "coordinate_space": "screen_xyxy",
        "image_size": [int(frame.shape[1]), int(frame.shape[0])],
        "detections": detections,
        "class_count": len(detections),
        "contract": config["contract"],
    }
    sys.stdout.write(json.dumps(result, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
