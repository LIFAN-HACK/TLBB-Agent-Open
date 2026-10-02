# -*- coding: utf-8 -*-
"""TLBB L2 slot worker.

Inputs are L1 crops. Full-frame fallbacks are allowed only when the caller also
passes the L1 bbox, and this worker crops before any slot logic runs.
"""
from __future__ import annotations

import argparse
import json
import sys
from collections import deque
from pathlib import Path

import numpy as np
from PIL import Image


ROOT = Path(__file__).resolve().parent
MINIMAP_MARKER_MANIFEST = ROOT / "templates" / "minimap_monster" / "manifest.json"
_MINIMAP_MARKER_CACHE: tuple[dict, list[tuple[str, np.ndarray]]] | None = None


def read_rgb(path: str) -> np.ndarray | None:
    try:
        return np.asarray(Image.open(path).convert("RGB"), dtype=np.uint8)
    except Exception:
        return None


def parse_bbox(value: str) -> list[int]:
    parts = [part.strip() for part in value.split(",")]
    if len(parts) != 4:
        raise argparse.ArgumentTypeError("bbox must be x1,y1,x2,y2")
    return [round(float(part)) for part in parts]


def crop_by_bbox(image: np.ndarray, bbox: list[int] | None) -> np.ndarray:
    if bbox is None:
        return image
    h, w = image.shape[:2]
    x1, y1, x2, y2 = bbox
    x1, x2 = max(0, min(w, x1)), max(0, min(w, x2))
    y1, y2 = max(0, min(h, y1)), max(0, min(h, y2))
    if x2 <= x1 or y2 <= y1:
        return image[0:0, 0:0]
    return image[y1:y2, x1:x2]


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
    if isinstance(value.get("center"), list) and len(value["center"]) == 2:
        enriched["screen_center"] = offset_point(value["center"], bbox)
    for key in ["slot_bbox", "bbox", "match_bbox"]:
        if isinstance(value.get(key), list) and len(value[key]) == 4:
            enriched[f"screen_{key}"] = offset_box(value[key], bbox)
    for key, child in list(enriched.items()):
        if key.startswith("screen_"):
            continue
        if isinstance(child, (dict, list)):
            enriched[key] = enrich_screen_coordinates(child, bbox)
    return enriched


def rel_box(crop: np.ndarray, xyxy: list[float]) -> np.ndarray:
    h, w = crop.shape[:2]
    x1 = max(0, min(w, round(xyxy[0] * w)))
    y1 = max(0, min(h, round(xyxy[1] * h)))
    x2 = max(0, min(w, round(xyxy[2] * w)))
    y2 = max(0, min(h, round(xyxy[3] * h)))
    return crop[y1:y2, x1:x2]


def rel_xyxy_to_pixels(crop: np.ndarray, xyxy: list[float]) -> list[int]:
    h, w = crop.shape[:2]
    return [
        max(0, min(w, round(xyxy[0] * w))),
        max(0, min(h, round(xyxy[1] * h))),
        max(0, min(w, round(xyxy[2] * w))),
        max(0, min(h, round(xyxy[3] * h))),
    ]


def color_fill_ratio(strip: np.ndarray, kind: str) -> float | None:
    if strip.size == 0:
        return None
    rgb = strip.astype(np.int16)
    if kind == "hp":
        mask = (rgb[:, :, 0] > 110) & (rgb[:, :, 0] > rgb[:, :, 1] * 1.25) & (rgb[:, :, 0] > rgb[:, :, 2] * 1.25)
    elif kind == "mp":
        mask = (rgb[:, :, 2] > 95) & (rgb[:, :, 2] > rgb[:, :, 0] * 1.15) & (rgb[:, :, 2] > rgb[:, :, 1] * 1.05)
    elif kind == "yellow":
        mask = (rgb[:, :, 0] > 140) & (rgb[:, :, 1] > 120) & (rgb[:, :, 2] < 90)
    else:
        return None
    columns = mask.mean(axis=0) > 0.25
    if not columns.any():
        return 0.0
    filled = np.flatnonzero(columns)
    return round((int(filled.max()) + 1) / mask.shape[1], 4)


def color_pixel_count(strip: np.ndarray, kind: str) -> int:
    if strip.size == 0:
        return 0
    rgb = strip.astype(np.int16)
    if kind == "hp":
        mask = (rgb[:, :, 0] > 110) & (rgb[:, :, 0] > rgb[:, :, 1] * 1.25) & (rgb[:, :, 0] > rgb[:, :, 2] * 1.25)
    elif kind == "mp":
        mask = (rgb[:, :, 2] > 95) & (rgb[:, :, 2] > rgb[:, :, 0] * 1.15) & (rgb[:, :, 2] > rgb[:, :, 1] * 1.05)
    elif kind == "yellow":
        mask = (rgb[:, :, 0] > 140) & (rgb[:, :, 1] > 120) & (rgb[:, :, 2] < 90)
    else:
        return 0
    return int(mask.sum())


def player_status(crop: np.ndarray) -> dict:
    # Normal 240x66 player panel: red HP at y33..38, blue MP at y39..44.
    hp_strip = rel_box(crop, [83 / 240, 33 / 66, 211 / 240, 38 / 66])
    mp_strip = rel_box(crop, [83 / 240, 39 / 66, 211 / 240, 44 / 66])
    yellow_strip = rel_box(crop, [83 / 240, 45 / 66, 211 / 240, 50 / 66])
    hp_pixels = color_pixel_count(hp_strip, "hp")
    mp_pixels = color_pixel_count(mp_strip, "mp")
    yellow_pixels = color_pixel_count(yellow_strip, "yellow")
    return {
        "ok": True,
        "slot": "player_status_panel",
        "panel_present": hp_pixels >= 8 or mp_pixels >= 8 or yellow_pixels >= 20,
        "hp_ratio": color_fill_ratio(hp_strip, "hp"),
        "mp_ratio": color_fill_ratio(mp_strip, "mp"),
        "hp_strip_bbox": rel_xyxy_to_pixels(crop, [83 / 240, 33 / 66, 211 / 240, 38 / 66]),
        "mp_strip_bbox": rel_xyxy_to_pixels(crop, [83 / 240, 39 / 66, 211 / 240, 44 / 66]),
        "hp_color_pixels": hp_pixels,
        "mp_color_pixels": mp_pixels,
        "yellow_color_pixels": yellow_pixels,
        "policy_hint": "release mana_restore_skill when mp_ratio is below configured threshold and skill cd_state is ready",
    }


def pet_status(crop: np.ndarray) -> dict:
    rgb = crop.astype(np.int16)
    red = (rgb[:, :, 0] > 115) & (rgb[:, :, 0] > rgb[:, :, 1] * 1.35) & (rgb[:, :, 0] > rgb[:, :, 2] * 1.35)
    green = (rgb[:, :, 1] > 80) & (rgb[:, :, 1] > rgb[:, :, 0] * 1.08) & (rgb[:, :, 1] > rgb[:, :, 2] * 1.08)
    # The pet HP strip is a stable 101x7 area inside the corrected 162x68 crop.
    # Measuring the complete panel confuses portrait/frame reds with HP fill.
    hp_strip = rel_box(crop, [53 / 162, 5 / 68, 155 / 162, 13 / 68])
    hp_ratio = color_fill_ratio(hp_strip, "hp")
    hp_pixels = color_pixel_count(hp_strip, "hp")
    state = "deployed" if int(red.sum()) >= 120 or int(green.sum()) >= 90 else "unknown"
    return {
        "ok": True,
        "slot": "pet_status_panel",
        "state": state,
        "hp_ratio": hp_ratio,
        "hp_color_pixels": hp_pixels,
        "hp_strip_bbox": rel_xyxy_to_pixels(crop, [53 / 162, 5 / 68, 155 / 162, 13 / 68]),
        "red_pixels": int(red.sum()),
        "green_pixels": int(green.sum()),
        "failure_policy": "if L1 pet_status_panel is absent, caller should report resting_or_absent; if crop exists but state is unknown, keep previous state or no-click",
    }


def connected_components(mask: np.ndarray, min_area: int = 6, max_area: int = 260) -> list[dict]:
    h, w = mask.shape
    seen = np.zeros_like(mask, dtype=bool)
    comps: list[dict] = []
    for y in range(h):
        for x in range(w):
            if seen[y, x] or not mask[y, x]:
                continue
            q: deque[tuple[int, int]] = deque([(x, y)])
            seen[y, x] = True
            pixels: list[tuple[int, int]] = []
            while q:
                cx, cy = q.popleft()
                pixels.append((cx, cy))
                for nx in (cx - 1, cx, cx + 1):
                    for ny in (cy - 1, cy, cy + 1):
                        if nx < 0 or ny < 0 or nx >= w or ny >= h or seen[ny, nx] or not mask[ny, nx]:
                            continue
                        seen[ny, nx] = True
                        q.append((nx, ny))
            area = len(pixels)
            if min_area <= area <= max_area:
                xs = [p[0] for p in pixels]
                ys = [p[1] for p in pixels]
                comps.append(
                    {
                        "area": area,
                        "center": [round(sum(xs) / area), round(sum(ys) / area)],
                        "bbox": [min(xs), min(ys), max(xs) + 1, max(ys) + 1],
                    }
                )
    return comps


def direct_ncc(left: np.ndarray, right: np.ndarray) -> float:
    if left.shape != right.shape or left.size == 0:
        return -1.0
    a = left.astype(np.float32)
    b = right.astype(np.float32)
    a -= float(a.mean())
    b -= float(b.mean())
    denominator = float(np.sqrt((a * a).sum()) * np.sqrt((b * b).sum()))
    return -1.0 if denominator <= 1e-8 else float((a * b).sum() / denominator)


def detect_minimap_monsters(crop: np.ndarray) -> dict:
    global _MINIMAP_MARKER_CACHE
    if not MINIMAP_MARKER_MANIFEST.exists():
        return {"state": "unknown", "matches": [], "best_score": -1.0, "reason": "marker templates missing"}
    if _MINIMAP_MARKER_CACHE is None:
        manifest = json.loads(MINIMAP_MARKER_MANIFEST.read_text(encoding="utf-8"))
        templates = []
        for relative_path in manifest.get("templates", []):
            template = read_rgb(str(MINIMAP_MARKER_MANIFEST.parent / relative_path))
            if template is not None:
                templates.append((relative_path, template))
        _MINIMAP_MARKER_CACHE = (manifest, templates)
    manifest, templates = _MINIMAP_MARKER_CACHE
    if not templates:
        return {"state": "unknown", "matches": [], "best_score": -1.0, "reason": "cannot read marker templates"}

    source_height, source_width = crop.shape[:2]
    if [source_width, source_height] == [240, 228]:
        analysis = crop
    else:
        analysis = np.asarray(Image.fromarray(crop).resize((240, 228), Image.Resampling.LANCZOS), dtype=np.uint8)
    height, width = analysis.shape[:2]
    yy, xx = np.ogrid[:height, :width]
    center_x, center_y = width * 0.53, height * 0.56
    radius = min(width, height) * 0.32
    inside_map = ((xx - center_x) ** 2 + (yy - center_y) ** 2) <= radius**2
    rgb = analysis.astype(np.int16)
    warm_core = (
        inside_map
        & (rgb[:, :, 0] > 180)
        & (rgb[:, :, 1] < 190)
        & (rgb[:, :, 2] < 100)
        & (rgb[:, :, 0] > rgb[:, :, 1] * 1.05)
    )
    candidates = connected_components(warm_core, min_area=3, max_area=160)
    hits: list[dict] = []
    best_score = -1.0
    best_candidate = None
    scales = [float(value) for value in manifest.get("scales", [0.65, 0.75, 0.85, 1.0, 1.15, 1.3])]
    for candidate in candidates:
        candidate_x, candidate_y = candidate["center"]
        # These are fixed warm compass/zoom buttons, not entities on the map.
        static_button = (
            width * 0.40 <= candidate_x <= width * 0.52 and height * 0.22 <= candidate_y <= height * 0.36
        ) or (
            width * 0.55 <= candidate_x <= width * 0.69 and height * 0.18 <= candidate_y <= height * 0.34
        )
        if candidate_y < height * 0.24 or static_button:
            continue
        center_rgb = [int(value) for value in analysis[candidate_y, candidate_x]]
        color_ok = center_rgb[0] >= 205 and center_rgb[2] <= 45 and center_rgb[0] - center_rgb[1] >= 65
        if not color_ok:
            continue
        for template_name, template in templates:
            for scale in scales:
                template_width = max(7, round(template.shape[1] * scale))
                template_height = max(7, round(template.shape[0] * scale))
                resized = np.asarray(
                    Image.fromarray(template).resize((template_width, template_height), Image.Resampling.LANCZOS),
                    dtype=np.uint8,
                )
                for offset_y in range(-3, 4):
                    for offset_x in range(-3, 4):
                        x1 = round(candidate_x + offset_x - template_width / 2)
                        y1 = round(candidate_y + offset_y - template_height / 2)
                        x2, y2 = x1 + template_width, y1 + template_height
                        if x1 < 0 or y1 < 0 or x2 > width or y2 > height:
                            continue
                        score = direct_ncc(analysis[y1:y2, x1:x2], resized)
                        if score > best_score:
                            best_score = score
                            best_candidate = {
                                "score": round(score, 4),
                                "bbox": [x1, y1, x2, y2],
                                "center": [round((x1 + x2) / 2), round((y1 + y2) / 2)],
                                "scale": scale,
                                "template": template_name,
                                "center_rgb": center_rgb,
                            }
                        if score >= float(manifest.get("presence_threshold", 0.73)):
                            hits.append(
                                {
                                    "score": round(score, 4),
                                    "bbox": [x1, y1, x2, y2],
                                    "center": [round((x1 + x2) / 2), round((y1 + y2) / 2)],
                                    "scale": scale,
                                    "template": template_name,
                                    "marker_type": "uu_target_ring" if template_name in {"orange_ring_1.png", "orange_ring_3.png"} else "native_or_uu_orange_dot",
                                    "center_rgb": center_rgb,
                                }
                            )

    hits.sort(key=lambda item: item["score"], reverse=True)
    matches: list[dict] = []
    for hit in hits:
        if any((hit["center"][0] - item["center"][0]) ** 2 + (hit["center"][1] - item["center"][1]) ** 2 < 64 for item in matches):
            continue
        matches.append(hit)
    absence_threshold = float(manifest.get("absence_threshold", 0.58))
    state = "present" if matches else "absent" if best_score < absence_threshold else "unknown"

    scale_x, scale_y = source_width / width, source_height / height

    def remap(item: dict | None) -> dict | None:
        if item is None:
            return None
        mapped = dict(item)
        mapped["analysis_bbox"] = item["bbox"]
        mapped["analysis_center"] = item["center"]
        mapped["bbox"] = [
            round(item["bbox"][0] * scale_x),
            round(item["bbox"][1] * scale_y),
            round(item["bbox"][2] * scale_x),
            round(item["bbox"][3] * scale_y),
        ]
        mapped["center"] = [round(item["center"][0] * scale_x), round(item["center"][1] * scale_y)]
        return mapped

    return {
        "state": state,
        "matches": [remap(item) for item in matches],
        "best_score": round(best_score, 4),
        "best_candidate": remap(best_candidate),
        "candidate_count": len(candidates),
        "analysis_size": [width, height],
        "presence_threshold": float(manifest.get("presence_threshold", 0.73)),
        "absence_threshold": absence_threshold,
        "reason": "matched warm circular marker template" if matches else "no marker above threshold" if state == "absent" else "ambiguous marker score",
    }


def detect_minimap_resources(crop: np.ndarray) -> dict:
    """Detect bright yellow diamond resource markers as explicit non-monsters."""
    source_height, source_width = crop.shape[:2]
    if [source_width, source_height] == [240, 228]:
        analysis = crop
    else:
        analysis = np.asarray(Image.fromarray(crop).resize((240, 228), Image.Resampling.LANCZOS), dtype=np.uint8)
    height, width = analysis.shape[:2]
    yy, xx = np.ogrid[:height, :width]
    center_x, center_y = width * 0.53, height * 0.56
    radius = min(width, height) * 0.32
    inside_map = ((xx - center_x) ** 2 + (yy - center_y) ** 2) <= radius**2
    rgb = analysis.astype(np.int16)
    warm_shape = (
        inside_map
        & (rgb[:, :, 0] > 175)
        & (rgb[:, :, 1] > 65)
        & (rgb[:, :, 2] < 100)
    )
    raw = connected_components(warm_shape, min_area=12, max_area=100)
    markers = []
    for item in raw:
        x1, y1, x2, y2 = item["bbox"]
        box_width, box_height = x2 - x1, y2 - y1
        fill_ratio = item["area"] / max(1, box_width * box_height)
        aspect = box_width / max(1, box_height)
        if not (5 <= box_width <= 12 and 5 <= box_height <= 12 and 0.72 <= aspect <= 1.38 and 0.25 <= fill_ratio <= 0.78):
            continue
        cx, cy = item["center"]
        center_rgb = [int(value) for value in analysis[cy, cx]]
        if not (center_rgb[1] >= 180 and center_rgb[0] - center_rgb[1] <= 70 and center_rgb[2] <= 70):
            continue
        if cy < height * 0.28:
            continue
        markers.append(
            {
                **item,
                "marker_type": "resource_marker",
                "fill_ratio": round(fill_ratio, 4),
                "center_rgb": center_rgb,
            }
        )

    scale_x, scale_y = source_width / width, source_height / height
    for item in markers:
        item["analysis_bbox"] = item["bbox"]
        item["analysis_center"] = item["center"]
        item["bbox"] = [
            round(item["bbox"][0] * scale_x),
            round(item["bbox"][1] * scale_y),
            round(item["bbox"][2] * scale_x),
            round(item["bbox"][3] * scale_y),
        ]
        item["center"] = [round(item["center"][0] * scale_x), round(item["center"][1] * scale_y)]
    return {"markers": markers, "count": len(markers), "analysis_size": [width, height]}


def cd_state_from_icon(slot_image: np.ndarray) -> dict:
    if slot_image.size == 0:
        return {"state": "unknown", "gray_ratio": 0.0, "digit_pixels": 0}
    rgb = slot_image.astype(np.int16)
    channel_max = rgb.max(axis=2)
    channel_min = rgb.min(axis=2)
    gray_ratio = float(((channel_max - channel_min < 38) & (channel_max < 175)).mean())
    cyan_digit = (rgb[:, :, 0] < 80) & (rgb[:, :, 1] > 125) & (rgb[:, :, 2] > 155)
    h, w = cyan_digit.shape
    digit_pixels = int(cyan_digit[round(h * 0.12) : round(h * 0.88), round(w * 0.12) : round(w * 0.88)].sum())
    if gray_ratio >= 0.62 or 18 <= digit_pixels <= 110:
        state = "cooling"
    elif gray_ratio < 0.56 and (digit_pixels < 18 or digit_pixels > 150):
        state = "ready"
    else:
        state = "unknown"
    return {"state": state, "gray_ratio": round(gray_ratio, 4), "digit_pixels": digit_pixels}


def hotkeys(crop: np.ndarray) -> dict:
    # Canonical skill_bar is 520x142. The current default only enables the
    # bottom hotkey row requested by the user. Slot identity is external policy.
    canonical = [
        ("F1", [0.010, 0.735, 0.071, 0.960]),
        ("F2", [0.079, 0.735, 0.140, 0.960]),
        ("F3", [0.148, 0.735, 0.210, 0.960]),
        ("F4", [0.217, 0.735, 0.279, 0.960]),
        ("F5", [0.286, 0.735, 0.348, 0.960]),
        ("F6", [0.355, 0.735, 0.417, 0.960]),
        ("F7", [0.424, 0.735, 0.486, 0.960]),
    ]
    h, w = crop.shape[:2]
    slots = []
    for key, box in canonical:
        x1, y1, x2, y2 = [round(box[0] * w), round(box[1] * h), round(box[2] * w), round(box[3] * h)]
        icon = crop[max(0, y1) : min(h, y2), max(0, x1) : min(w, x2)]
        cd = cd_state_from_icon(icon)
        slots.append(
            {
                "key": key,
                "slot_bbox": [x1, y1, x2, y2],
                "click_point": [round((x1 + x2) / 2), round((y1 + y2) / 2)],
                "cd_state": cd["state"],
                "cd_features": {k: v for k, v in cd.items() if k != "state"},
            }
        )
    return {"ok": True, "slot": "skill_bar_hotkeys", "slots": slots}


def skill_bar_slots(crop: np.ndarray, matcher: object | None) -> dict:
    canonical_width, canonical_height = 520, 142
    canonical_x, canonical_step, slot_size = 5, 36, 32
    row_y = (22, 58, 104)
    bottom_keys = ("Q", "W", "E", "R", "A", "S", "D", "Z", "X", "C", "", "", "", "")
    h, w = crop.shape[:2]
    scale_x = w / canonical_width
    scale_y = h / canonical_height
    slots = []
    for row, y in enumerate(row_y):
        for column in range(14):
            x = canonical_x + column * canonical_step
            x1, y1 = round(x * scale_x), round(y * scale_y)
            x2, y2 = round((x + slot_size) * scale_x), round((y + slot_size) * scale_y)
            icon = crop[max(0, y1) : min(h, y2), max(0, x1) : min(w, x2)]
            identity = matcher.match(icon, mask_key_hint=row == 2) if matcher is not None and icon.size else {
                "skill_id": "",
                "name": "",
                "score": 0.0,
                "margin": 0.0,
                "status": "unknown",
                "identity_source": "unavailable",
                "hang_usable": False,
                "policy_class": "",
                "release_rule": "identity_unavailable",
            }
            cd = cd_state_from_icon(icon)
            if (
                identity.get("status") == "matched"
                and identity.get("template_cd_state") == "cooling"
                and float(identity.get("score", 0.0)) >= 0.92
                and cd["state"] != "cooling"
            ):
                cd["state"] = "cooling"
                cd["conservative_template_override"] = True
            key = bottom_keys[column] if row == 2 else ""
            surface = "bottom_skill_bar" if row == 2 else "unkeyed_skill_bar"
            activation_kind = "keyboard" if key else "none"
            slots.append(
                {
                    "slot_id": f"r{row + 1}c{column + 1}",
                    "row": row,
                    "column": column,
                    "surface": surface,
                    "key_hint": key,
                    "activation_kind": activation_kind,
                    "slot_bbox": [x1, y1, x2, y2],
                    "icon_crop": {
                        "source": "l1_skill_bar_content_crop",
                        "bbox": [x1, y1, x2, y2],
                        "size": [int(icon.shape[1]), int(icon.shape[0])],
                    },
                    "click_point": [round((x1 + x2) / 2), round((y1 + y2) / 2)],
                    "cd_state": cd["state"],
                    "cd_features": {key: value for key, value in cd.items() if key != "state"},
                    **identity,
                    "castable": bool(
                        identity.get("status") == "matched"
                        and identity.get("identity_source") == "icons_ingame"
                        and identity.get("hang_usable")
                        and cd["state"] == "ready"
                        and key
                    ),
                }
            )
    return {
        "ok": True,
        "slot": "skill_bar_slots",
        "grid": {"rows": 3, "columns": 14, "canonical_size": [canonical_width, canonical_height]},
        "runtime_match_source": "icons_ingame_only" if matcher is not None else "unavailable",
        "slots": slots,
    }


def dynamic_skill_slots(
    crop: np.ndarray,
    matcher: object | None,
    slot_specs: list[dict],
    surface: str,
) -> dict:
    h, w = crop.shape[:2]
    slots = []
    for spec in slot_specs:
        box = spec.get("slot_bbox", [])
        if not isinstance(box, list) or len(box) != 4:
            continue
        x1, y1, x2, y2 = [int(round(value)) for value in box]
        if x1 < 0 or y1 < 0 or x2 > w or y2 > h or x2 <= x1 or y2 <= y1:
            continue
        icon = crop[y1:y2, x1:x2]
        identity = matcher.match(icon, mask_key_hint=False) if matcher is not None and icon.size else {
            "skill_id": "",
            "name": "",
            "score": 0.0,
            "margin": 0.0,
            "status": "unknown",
            "identity_source": "unavailable",
            "hang_usable": False,
            "policy_class": "",
            "release_rule": "identity_unavailable",
        }
        cd = cd_state_from_icon(icon)
        if (
            identity.get("status") == "matched"
            and identity.get("template_cd_state") == "cooling"
            and float(identity.get("score", 0.0)) >= 0.92
            and cd["state"] != "cooling"
        ):
            cd["state"] = "cooling"
            cd["conservative_template_override"] = True
        activation_kind = "none" if surface == "buff_bar" else "mouse"
        slots.append(
            {
                "index": int(spec.get("index", len(slots))),
                "slot_id": str(spec.get("slot_id", "")),
                "row": int(spec.get("row", 0)),
                "column": int(spec.get("column", 0)),
                "surface": surface,
                "key_hint": "",
                "activation_kind": activation_kind,
                "slot_bbox": [x1, y1, x2, y2],
                "click_point": [round((x1 + x2) / 2), round((y1 + y2) / 2)],
                "cd_state": cd["state"],
                "cd_features": {key: value for key, value in cd.items() if key != "state"},
                **identity,
                "castable": bool(
                    surface != "buff_bar"
                    and identity.get("status") == "matched"
                    and identity.get("identity_source") == "icons_ingame"
                    and identity.get("hang_usable")
                    and cd["state"] == "ready"
                ),
            }
        )
    return {
        "ok": True,
        "slot": "dynamic_skill_slots",
        "surface": surface,
        "runtime_match_source": "icons_ingame_only" if matcher is not None else "unavailable",
        "slots": slots,
    }


def minimap(crop: np.ndarray) -> dict:
    h, w = crop.shape[:2]
    yy, xx = np.ogrid[:h, :w]
    cx, cy = w * 0.53, h * 0.56
    radius = min(w, h) * 0.32
    circle = ((xx - cx) ** 2 + (yy - cy) ** 2) <= radius**2
    rgb = crop.astype(np.int16)
    blue_mask = circle & (rgb[:, :, 2] > 140) & (rgb[:, :, 1] > 90) & (rgb[:, :, 0] < 120)
    players = connected_components(blue_mask)
    marker_result = detect_minimap_monsters(crop)
    resource_result = detect_minimap_resources(crop)
    # The coordinate digits are the small gold row directly below the map name.
    # Keep this tight so the right-most digit and dragon ornament do not enter OCR.
    coord_roi = [0.43, 0.16, 0.73, 0.22]
    map_name_roi = [0.25, 0.03, 0.80, 0.16]
    return {
        "ok": True,
        "slot": "minimap",
        "decision_scope": "single_frame_observation",
        "frame_marker_state": marker_result["state"],
        "nearby_monster_state": marker_result["state"],
        "nearby_monster_present": True if marker_result["state"] == "present" else False if marker_result["state"] == "absent" else None,
        "nearby_monster_count": len(marker_result["matches"]),
        "monster_markers": marker_result["matches"],
        "uu_target_ring_count": sum(item.get("marker_type") == "uu_target_ring" for item in marker_result["matches"]),
        "native_or_uu_orange_dot_count": sum(item.get("marker_type") == "native_or_uu_orange_dot" for item in marker_result["matches"]),
        "monster_marker_diagnostics": {key: value for key, value in marker_result.items() if key not in {"state", "matches"}},
        "resource_marker_count": resource_result["count"],
        "resource_markers": resource_result["markers"],
        "resource_policy": "resource_marker is an explicit non-monster and must never enable attack polling",
        "blue_marker_count": len(players),
        "blue_markers": players,
        "coord_text_roi_normalized": coord_roi,
        "coord_text_roi": rel_xyxy_to_pixels(crop, coord_roi),
        "map_name_roi_normalized": map_name_roi,
        "map_name_roi": rel_xyxy_to_pixels(crop, map_name_roi),
        "coordinate_reader": "send the L1 minimap crop to minimap_coordinate_worker.py",
        "absence_policy": "single-frame absent is weak evidence; send consecutive results to minimap_temporal_fusion.py",
    }


def material_panel(crop: np.ndarray) -> dict:
    rgb = crop.astype(np.int16)
    h, w = crop.shape[:2]
    dark = (rgb[:, :, 0] < 55) & (rgb[:, :, 1] < 55) & (rgb[:, :, 2] < 55)
    gold = (rgb[:, :, 0] > 130) & (rgb[:, :, 1] > 85) & (rgb[:, :, 1] < rgb[:, :, 0]) & (rgb[:, :, 2] < 80)
    panel_like = float(dark.mean())
    raw_button_components = connected_components(gold, min_area=18, max_area=1400)
    button_components = []
    for item in raw_button_components:
        x1, y1, x2, y2 = item["bbox"]
        box_w = x2 - x1
        box_h = y2 - y1
        center_x, center_y = item["center"]
        plausible_size = 12 <= box_w <= 220 and 4 <= box_h <= 80 and item["area"] >= 40
        not_top_frame_noise = center_y >= round(h * 0.10)
        inside_main_area = round(w * 0.02) <= center_x <= round(w * 0.98)
        if plausible_size and not_top_frame_noise and inside_main_area:
            button_components.append(item)
    button_components.sort(key=lambda item: item["area"], reverse=True)
    progress_strip = rel_box(crop, [0.28, 0.72, 0.74, 0.86])
    progress_ratio = color_fill_ratio(progress_strip, "yellow")
    return {
        "ok": True,
        "slot": "material_or_dialog_panel",
        "crop_size": [w, h],
        "panel_like_score": round(panel_like, 4),
        "raw_gold_button_candidate_count": len(raw_button_components),
        "button_candidate_ambiguity": "high" if len(raw_button_components) > 80 and (w * h) > 500000 else "normal",
        "gold_button_candidates": button_components[:20],
        "progress_ratio": progress_ratio,
        "state": "panel_present" if panel_like > 0.18 and button_components else "unknown",
        "note": "material processing still needs more explicit positive samples before promoting exact button names",
    }


def policy_panel(crop: np.ndarray, slot_name: str, ocr_text: str = "") -> dict:
    base = material_panel(crop)
    policies = {
        "monthly_card": {
            "slot": "monthly_card_panel",
            "required_ocr_keywords": ["月卡", "领取", "奖励", "福利", "确定"],
            "safe_action": "click only after OpenText confirms monthly-card claim text and a configured button candidate",
        },
        "town_return": {
            "slot": "town_return_panel",
            "required_ocr_keywords": ["回城", "返回", "传送", "驿站", "确定"],
            "safe_action": "trigger return action, then verify map_name/coord_text from minimap OCR before continuing",
        },
        "shop_cleanup": {
            "slot": "shop_cleanup_panel",
            "required_ocr_keywords": ["商店", "出售", "修理", "背包", "确定"],
            "safe_action": "sell only items allowed by an external keep/sell policy; never infer item value from color alone",
        },
        "auto_nav": {
            "slot": "auto_navigation_panel",
            "required_ocr_keywords": ["自动寻路", "寻路", "坐标", "到达", "停止"],
            "safe_action": "read map_name/coord_text by OpenText and use script-side path policy; no dedicated nav model is available yet",
        },
    }
    policy = policies[slot_name]
    normalized_text = ocr_text.strip()
    matched_keywords = [keyword for keyword in policy["required_ocr_keywords"] if keyword in normalized_text]
    primary_button = None
    candidate_usable = base.get("button_candidate_ambiguity") != "high"
    if candidate_usable and base["gold_button_candidates"]:
        primary_button = base["gold_button_candidates"][0]
        primary_button = {
            "bbox": primary_button["bbox"],
            "center": primary_button["center"],
            "area": primary_button["area"],
        }
    confirmed = bool(matched_keywords) and primary_button is not None
    base.update(
        {
            "slot": policy["slot"],
            "policy_state": "confirmed_candidate" if confirmed else "needs_opentext_confirmation",
            "required_ocr_keywords": policy["required_ocr_keywords"],
            "matched_ocr_keywords": matched_keywords,
            "ocr_text_provided": bool(normalized_text),
            "primary_button_candidate": primary_button,
            "button_candidate_usable": candidate_usable,
            "safe_action": policy["safe_action"],
            "sample_status": "weak_or_missing_positive_samples",
        }
    )
    return base


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--mode",
        required=True,
        choices=["player", "pet", "hotkeys", "minimap", "material", "monthly_card", "town_return", "shop_cleanup", "auto_nav"],
    )
    parser.add_argument("--image", required=True)
    parser.add_argument("--bbox", type=parse_bbox, default=None, help="Optional L1 bbox in full-frame coordinates.")
    parser.add_argument("--ocr-text", default="", help="Optional OpenText result for policy confirmation.")
    args = parser.parse_args()

    image = read_rgb(args.image)
    if image is None:
        result = {"ok": False, "error": "cannot read image"}
    else:
        crop = crop_by_bbox(image, args.bbox)
        if crop.size == 0:
            result = {"ok": False, "error": "empty crop"}
        elif args.mode == "player":
            result = player_status(crop)
        elif args.mode == "pet":
            result = pet_status(crop)
        elif args.mode == "hotkeys":
            result = hotkeys(crop)
        elif args.mode == "minimap":
            result = minimap(crop)
        elif args.mode == "material":
            result = material_panel(crop)
        else:
            result = policy_panel(crop, args.mode, args.ocr_text)
        if result.get("ok"):
            result["coordinate_space"] = "crop_xyxy"
            result["input_bbox"] = args.bbox
            result = enrich_screen_coordinates(result, args.bbox)
    sys.stdout.write(json.dumps(result, ensure_ascii=False))
    return 0 if result.get("ok") else 2


if __name__ == "__main__":
    raise SystemExit(main())
