# -*- coding: utf-8 -*-
"""Fixed TLBB UI slot recognizer.

This worker consumes an L1 skill_bar crop. When a full frame plus --skill-bbox
is provided, it first crops that L1 box and then runs all template logic inside
the crop. It never searches the full screen for skill icons.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image


ROOT = Path(__file__).resolve().parent
CONFIG_PATH = ROOT / "inference_config.json"


def imread_unicode(path: str) -> np.ndarray | None:
    try:
        return np.asarray(Image.open(path).convert("RGB"), dtype=np.uint8)
    except Exception:
        return None


def parse_bbox(value: str) -> list[int]:
    parts = [part.strip() for part in value.split(",")]
    if len(parts) != 4:
        raise argparse.ArgumentTypeError("bbox must be x1,y1,x2,y2")
    try:
        return [round(float(part)) for part in parts]
    except ValueError as exc:
        raise argparse.ArgumentTypeError("bbox values must be numeric") from exc


def clamp_bbox(box: list[int], width: int, height: int) -> list[int]:
    x1, y1, x2, y2 = box
    return [max(0, min(width, x1)), max(0, min(height, y1)), max(0, min(width, x2)), max(0, min(height, y2))]


def scale_slot(canonical: list[float], skill_bbox: list[int]) -> list[int]:
    x1, y1, x2, y2 = skill_bbox
    scale_x = x2 - x1
    scale_y = y2 - y1
    return [
        round(x1 + canonical[0] * scale_x),
        round(y1 + canonical[1] * scale_y),
        round(x1 + canonical[2] * scale_x),
        round(y1 + canonical[3] * scale_y),
    ]


def offset_point(point: list[int], bbox: list[int]) -> list[int]:
    return [bbox[0] + int(point[0]), bbox[1] + int(point[1])]


def offset_box(box: list[int], bbox: list[int]) -> list[int]:
    return [bbox[0] + int(box[0]), bbox[1] + int(box[1]), bbox[0] + int(box[2]), bbox[1] + int(box[3])]


def enrich_screen_coordinates(value: object, bbox: list[int] | None) -> object:
    if bbox is None:
        return value
    if isinstance(value, list):
        return [enrich_screen_coordinates(item, bbox) for item in value]
    if not isinstance(value, dict):
        return value
    enriched = dict(value)
    if isinstance(value.get("click_point"), list) and len(value["click_point"]) == 2:
        enriched["screen_click_point"] = offset_point(value["click_point"], bbox)
    for key in ["slot_bbox", "match_bbox", "bbox"]:
        if isinstance(value.get(key), list) and len(value[key]) == 4:
            enriched[f"screen_{key}"] = offset_box(value[key], bbox)
    for key, child in list(enriched.items()):
        if key.startswith("screen_"):
            continue
        if isinstance(child, (dict, list)):
            enriched[key] = enrich_screen_coordinates(child, bbox)
    return enriched


def load_templates(config: dict) -> list[dict]:
    templates: list[dict] = []
    for business_class, variants in config["business_classes"].items():
        for variant in variants:
            path = ROOT / "templates" / "pet_restore" / f"{variant}_32.png"
            image = imread_unicode(str(path))
            if image is None:
                raise FileNotFoundError(f"cannot read template: {path}")
            templates.append(
                {
                    "variant": variant,
                    "business_class": business_class,
                    "path": str(path),
                    "image": image,
                }
            )
    return templates


def match_template_in_slot(
    frame: np.ndarray,
    slot_bbox: list[int],
    template: np.ndarray,
    padding: int,
) -> tuple[float, list[int]]:
    frame_height, frame_width = frame.shape[:2]
    x1, y1, x2, y2 = slot_bbox
    search_bbox = clamp_bbox([x1 - padding, y1 - padding, x2 + padding, y2 + padding], frame_width, frame_height)
    sx1, sy1, sx2, sy2 = search_bbox
    search = frame[sy1:sy2, sx1:sx2]
    slot_width = max(1, x2 - x1)
    slot_height = max(1, y2 - y1)
    resized = np.asarray(
        Image.fromarray(template).resize((slot_width, slot_height), Image.Resampling.LANCZOS),
        dtype=np.uint8,
    )
    if search.shape[0] < resized.shape[0] or search.shape[1] < resized.shape[1]:
        return -1.0, slot_bbox
    tpl = resized.astype(np.float32)
    tpl = tpl - tpl.mean(axis=(0, 1), keepdims=True)
    tpl_norm = float(np.sqrt((tpl * tpl).sum()))
    if tpl_norm <= 1e-8:
        return -1.0, slot_bbox
    best_score = -1.0
    best_xy = (sx1, sy1)
    max_y = search.shape[0] - slot_height
    max_x = search.shape[1] - slot_width
    for yy in range(max_y + 1):
        for xx in range(max_x + 1):
            patch = search[yy : yy + slot_height, xx : xx + slot_width].astype(np.float32)
            patch = patch - patch.mean(axis=(0, 1), keepdims=True)
            denom = float(np.sqrt((patch * patch).sum()) * tpl_norm)
            score = -1.0 if denom <= 1e-8 else float((patch * tpl).sum() / denom)
            if score > best_score:
                best_score = score
                best_xy = (sx1 + xx, sy1 + yy)
    match_x, match_y = best_xy
    return best_score, [match_x, match_y, match_x + slot_width, match_y + slot_height]


def identity_state(score: float, margin: float, config: dict) -> str:
    ambiguity_margin = float(config["template_ambiguity_margin_min"])
    if margin < ambiguity_margin:
        return "unknown"
    if score >= float(config["template_match_threshold"]):
        return "matched"
    return "unknown"


def detect_cd_state(slot_image: np.ndarray, identity: str) -> dict:
    if identity == "unknown":
        return {"state": "unknown", "gray_ratio": 0.0, "digit_pixels": 0}
    rgb = slot_image.astype(np.int16)
    channel_max = rgb.max(axis=2)
    channel_min = rgb.min(axis=2)
    saturation_like = channel_max - channel_min
    gray_mask = (saturation_like < 38) & (channel_max < 175)
    gray_ratio = float(gray_mask.mean())
    bright = (
        ((rgb[:, :, 0] > 190) & (rgb[:, :, 1] > 150) & (rgb[:, :, 2] < 120))
        | ((rgb[:, :, 0] > 200) & (rgb[:, :, 1] > 200) & (rgb[:, :, 2] > 200))
    )
    # Cooldown digits usually sit in the upper/center part of the icon. Count
    # only that area so normal bright icon details do not dominate.
    h, w = bright.shape
    digit_pixels = int(bright[: max(1, round(h * 0.65)), round(w * 0.15) : round(w * 0.85)].sum())
    if gray_ratio >= 0.42 or digit_pixels >= 18:
        return {"state": "cooling", "gray_ratio": round(gray_ratio, 4), "digit_pixels": digit_pixels}
    return {"state": "ready", "gray_ratio": round(gray_ratio, 4), "digit_pixels": digit_pixels}


def recognize_pet_restore(skill_crop: np.ndarray, config: dict, templates: list[dict]) -> dict:
    crop_height, crop_width = skill_crop.shape[:2]
    skill_bbox = [0, 0, crop_width, crop_height]

    scale_x = crop_width / float(config["canonical_skill_bar_size"][0])
    scale_y = crop_height / float(config["canonical_skill_bar_size"][1])
    padding = max(2, round(float(config["template_search_padding"]) * max(scale_x, scale_y)))
    slots: list[dict] = []
    detections: list[dict] = []
    slot_config = config.get("pet_active_slots_normalized") or config["pet_active_slots"]
    for slot_name, canonical_slot in slot_config.items():
        if max(canonical_slot) > 1:
            canonical_size = config["canonical_skill_bar_size"]
            canonical_slot = [
                canonical_slot[0] / canonical_size[0],
                canonical_slot[1] / canonical_size[1],
                canonical_slot[2] / canonical_size[0],
                canonical_slot[3] / canonical_size[1],
            ]
        slot_bbox = scale_slot(canonical_slot, skill_bbox)
        candidates: list[dict] = []
        for template in templates:
            score, match_bbox = match_template_in_slot(skill_crop, slot_bbox, template["image"], padding)
            candidates.append(
                {
                    "variant": template["variant"],
                    "business_class": template["business_class"],
                    "score": score,
                    "bbox": match_bbox,
                }
            )
        candidates.sort(key=lambda item: item["score"], reverse=True)
        best = candidates[0]
        runner_up = candidates[1]
        margin = float(best["score"] - runner_up["score"])
        state = identity_state(float(best["score"]), margin, config)
        matched = state == "matched"
        x1, y1, x2, y2 = clamp_bbox(best["bbox"], crop_width, crop_height)
        slot_pixels = skill_crop[y1:y2, x1:x2] if x2 > x1 and y2 > y1 else skill_crop[0:0, 0:0]
        cd = detect_cd_state(slot_pixels, state) if slot_pixels.size else {"state": "unknown", "gray_ratio": 0.0, "digit_pixels": 0}
        result = {
            "slot": slot_name,
            "slot_bbox": slot_bbox,
            "variant": best["variant"] if matched else None,
            "business_class": best["business_class"] if matched else None,
            "identity_state": state,
            "cd_state": cd["state"],
            "cd_features": {key: value for key, value in cd.items() if key != "state"},
            "score": round(float(best["score"]), 4),
            "margin": round(margin, 4),
            "runner_up": runner_up["variant"],
            "match_bbox": best["bbox"],
            "click_point": [
                round((best["bbox"][0] + best["bbox"][2]) / 2),
                round((best["bbox"][1] + best["bbox"][3]) / 2),
            ],
        }
        slots.append(result)
        if matched:
            detections.append(result)

    return {
        "ok": True,
        "engine": "fixed_ui_slot_worker",
        "input_mode": "l1_skill_bar_crop",
        "skill_crop_size": [crop_width, crop_height],
        "slots": slots,
        "detections": detections,
        "clickable": [item for item in detections if item["cd_state"] == "ready"],
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--image", required=True)
    parser.add_argument("--skill-bbox", required=False, type=parse_bbox)
    parser.add_argument(
        "--skill-crop",
        action="store_true",
        help="Treat the whole image as an L1 skill_bar crop.",
    )
    args = parser.parse_args()

    frame = imread_unicode(args.image)
    if frame is None:
        result = {"ok": False, "error": "cannot read image"}
    else:
        config = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
        templates = load_templates(config)
        screen_origin_bbox = None
        if args.skill_crop:
            skill_crop = frame
        elif args.skill_bbox:
            full_height, full_width = frame.shape[:2]
            x1, y1, x2, y2 = clamp_bbox(args.skill_bbox, full_width, full_height)
            screen_origin_bbox = [x1, y1, x2, y2]
            skill_crop = frame[y1:y2, x1:x2]
        else:
            result = {"ok": False, "error": "provide --skill-bbox or --skill-crop"}
            sys.stdout.write(json.dumps(result, ensure_ascii=False))
            return 2
        if skill_crop.size == 0:
            result = {"ok": False, "error": "empty L1 skill_bar crop"}
        else:
            result = recognize_pet_restore(skill_crop, config, templates)
            result["coordinate_space"] = "crop_xyxy"
            result["input_bbox"] = screen_origin_bbox
            result = enrich_screen_coordinates(result, screen_origin_bbox)

    sys.stdout.write(json.dumps(result, ensure_ascii=False))
    sys.stdout.flush()
    return 0 if result.get("ok") else 2


if __name__ == "__main__":
    raise SystemExit(main())
