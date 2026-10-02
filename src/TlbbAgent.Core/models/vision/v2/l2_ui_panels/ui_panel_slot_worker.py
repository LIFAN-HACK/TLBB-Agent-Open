from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

import numpy as np
from PIL import Image


ROOT = Path(__file__).resolve().parent
CONTROL_TEMPLATE_MANIFEST = ROOT / "control_template_manifest.json"
TEMPLATE_CONFIRMABLE = {
    "material_panel",
    "monthly_card_panel",
    "shop_panel",
    "bag_panel",
    "bank_panel",
    "death_dialog",
    "auto_nav_panel",
}

PANEL_SPECS = {
    "material_panel": {
        "ocr_rois": {"title": [0.18, 0.00, 0.82, 0.10], "body": [0.03, 0.08, 0.97, 0.90]},
        "controls": {
            "scroll_up": [0.882, 0.176, 0.934, 0.240],
            "scroll_down": [0.882, 0.397, 0.934, 0.463],
            "manufacture_all": [0.02, 0.91, 0.30, 0.99],
            "quantity_decrease": [0.36, 0.91, 0.43, 0.99],
            "quantity_value": [0.44, 0.91, 0.59, 0.99],
            "quantity_increase": [0.60, 0.91, 0.67, 0.99],
            "manufacture": [0.69, 0.91, 0.84, 0.99],
            "close": [0.85, 0.91, 0.98, 0.99],
        },
        "keyword_groups": [["\u914d\u65b9", "\u6d3b\u529b"], ["\u914d\u65b9", "\u5236\u9020"]],
        "safe_actions": ["scroll_up", "scroll_down", "manufacture", "manufacture_all", "close"],
    },
    "monthly_card_panel": {
        "ocr_rois": {"title": [0.22, 0.00, 0.80, 0.10], "body": [0.02, 0.08, 0.98, 0.98]},
        "controls": {
            "open_portable_pet_shop": [0.52, 0.42, 0.98, 0.54],
            "open_portable_bank": [0.02, 0.54, 0.49, 0.66],
            "open_portable_pet_bank": [0.52, 0.54, 0.98, 0.66],
            "repair_equipment": [0.02, 0.66, 0.49, 0.78],
            "repair_weapon": [0.02, 0.78, 0.49, 0.90],
            "repair_artifact": [0.52, 0.78, 0.98, 0.90],
            "close": [0.92, 0.00, 1.00, 0.10],
        },
        "keyword_groups": [["\u968f\u8eab\u73cd\u517d\u5546\u5e97", "\u968f\u8eab\u94f6\u884c"], ["\u88c5\u5907\u4fee\u7406", "\u795e\u5668\u4fee\u7406"]],
        "safe_actions": ["open_portable_pet_shop", "open_portable_bank", "repair_equipment", "repair_weapon", "repair_artifact", "close"],
    },
    "shop_panel": {
        "ocr_rois": {"body": [0.02, 0.06, 0.98, 0.82], "sale_slots": [0.02, 0.81, 0.98, 0.99]},
        "controls": {
            "sell": [0.76, 0.73, 0.98, 0.82],
            "batch_buy": [0.70, 0.82, 0.98, 0.91],
            "close": [0.92, 0.00, 1.00, 0.10],
        },
        "keyword_groups": [["\u51fa\u552e", "\u6279\u91cf\u8d2d\u4e70"]],
        "safe_actions": ["sell", "close"],
    },
    "bag_panel": {
        "ocr_rois": {"title": [0.30, 0.00, 0.72, 0.10], "item_grid": [0.02, 0.13, 0.98, 0.64]},
        "controls": {
            "organize": [0.28, 0.65, 0.45, 0.74],
            "lock": [0.56, 0.65, 0.72, 0.74],
            "split": [0.82, 0.65, 0.98, 0.74],
            "close": [0.92, 0.00, 1.00, 0.10],
        },
        "keyword_groups": [["\u6574\u7406", "\u9501\u5b9a", "\u62c6\u5206"]],
        "safe_actions": ["organize", "close"],
    },
    "bank_panel": {
        "ocr_rois": {"title": [0.35, 0.00, 0.65, 0.10], "item_grid": [0.02, 0.10, 0.98, 0.84]},
        "controls": {
            "organize": [0.84, 0.90, 0.90, 0.99],
            "deposit": [0.91, 0.90, 0.95, 0.99],
            "withdraw": [0.96, 0.90, 1.00, 0.99],
            "close": [0.96, 0.00, 1.00, 0.10],
        },
        "keyword_groups": [["\u94b1\u5e84", "\u53d6\u94b1"], ["\u5b58\u94b1", "\u53d6\u94b1"]],
        "safe_actions": ["organize", "close"],
    },
    "death_dialog": {
        "ocr_rois": {"body": [0.02, 0.12, 0.98, 0.70]},
        "controls": {
            "revive": [0.55, 0.70, 0.76, 0.98],
            "exit": [0.78, 0.70, 0.98, 0.98],
        },
        "keyword_groups": [["\u6b7b\u4ea1", "\u590d\u6d3b"], ["\u590d\u6d3b", "\u51fa\u7a8d"]],
        "safe_actions": ["revive", "exit"],
    },
    "npc_destination_panel": {
        "ocr_rois": {"title": [0.25, 0.00, 0.75, 0.10], "destination_list": [0.03, 0.20, 0.97, 0.86]},
        "controls": {
            "accept": [0.02, 0.90, 0.22, 0.99],
            "continue": [0.23, 0.90, 0.43, 0.99],
            "goodbye": [0.78, 0.90, 0.98, 0.99],
        },
        "keyword_groups": [["\u6d1b\u9633", "\u82cf\u5dde"], ["\u5927\u7406", "\u675f\u6cb3\u53e4\u9547"]],
        "safe_actions": ["accept", "continue", "goodbye"],
    },
    "auto_nav_panel": {
        "ocr_rois": {"title": [0.15, 0.00, 0.85, 0.18], "list": [0.02, 0.24, 0.98, 0.82], "coord": [0.05, 0.82, 0.75, 1.00]},
        "controls": {"move": [0.73, 0.82, 0.99, 1.00], "close": [0.90, 0.00, 1.00, 0.18]},
        "keyword_groups": [["\u81ea\u52a8\u5bfb\u8def", "\u5750\u6807"], ["\u5750\u6807", "\u79fb\u52a8"]],
        "safe_actions": ["move", "close"],
    },
    "confirm_dialog": {
        "ocr_rois": {"body": [0.02, 0.10, 0.98, 0.78]},
        "controls": {"confirm": [0.63, 0.78, 0.82, 0.98], "cancel": [0.82, 0.78, 0.99, 0.98]},
        "keyword_groups": [["\u786e\u8ba4", "\u81ea\u52a8\u5bfb\u8def"], ["\u5f00\u542f", "\u81ea\u52a8\u5bfb\u8def"], ["\u786e\u5b9a", "\u53d6\u6d88"]],
        "safe_actions": ["confirm", "cancel"],
    },
}


def read_rgb(path: str) -> np.ndarray | None:
    try:
        return np.asarray(Image.open(path).convert("RGB"), dtype=np.uint8)
    except Exception:
        return None


def parse_bbox(value: str) -> list[int]:
    parts = [round(float(item.strip())) for item in value.split(",")]
    if len(parts) != 4:
        raise argparse.ArgumentTypeError("bbox must be x1,y1,x2,y2")
    return parts


def crop_by_bbox(image: np.ndarray, bbox: list[int] | None) -> np.ndarray:
    if bbox is None:
        return image
    h, w = image.shape[:2]
    x1, y1, x2, y2 = bbox
    x1, x2 = max(0, min(w, x1)), max(0, min(w, x2))
    y1, y2 = max(0, min(h, y1)), max(0, min(h, y2))
    return image[y1:y2, x1:x2] if x2 > x1 and y2 > y1 else image[0:0, 0:0]


def normalized_text(value: str) -> str:
    return re.sub(r"[\s\u3000,.;:!?/\\|_\-]+", "", value or "")


def pixel_box(box: list[float], width: int, height: int) -> list[int]:
    return [round(box[0] * width), round(box[1] * height), round(box[2] * width), round(box[3] * height)]


def center(box: list[int]) -> list[int]:
    return [round((box[0] + box[2]) / 2), round((box[1] + box[3]) / 2)]


def screen_box(box: list[int], bbox: list[int] | None) -> list[int]:
    if bbox is None:
        return box
    return [bbox[0] + box[0], bbox[1] + box[1], bbox[0] + box[2], bbox[1] + box[3]]


def screen_point(point: list[int], bbox: list[int] | None) -> list[int]:
    if bbox is None:
        return point
    return [bbox[0] + point[0], bbox[1] + point[1]]


def group_matches(text: str, groups: list[list[str]]) -> tuple[bool, list[str]]:
    compact = normalized_text(text)
    best: list[str] = []
    for group in groups:
        matched = [keyword for keyword in group if normalized_text(keyword) in compact]
        if len(matched) > len(best):
            best = matched
        if len(matched) == len(group):
            return True, matched
    return False, best


def panel_features(crop: np.ndarray) -> dict:
    rgb = crop.astype(np.int16)
    dark = (rgb[:, :, 0] < 65) & (rgb[:, :, 1] < 65) & (rgb[:, :, 2] < 65)
    gold = (rgb[:, :, 0] > 105) & (rgb[:, :, 1] > 60) & (rgb[:, :, 1] < rgb[:, :, 0] * 0.92) & (rgb[:, :, 2] < 80)
    return {"dark_ratio": round(float(dark.mean()), 4), "gold_ratio": round(float(gold.mean()), 4)}


def direct_ncc(left: np.ndarray, right: np.ndarray) -> float:
    if left.shape != right.shape or left.size == 0:
        return -1.0
    a = left.astype(np.float32) - left.astype(np.float32).mean(axis=(0, 1), keepdims=True)
    b = right.astype(np.float32) - right.astype(np.float32).mean(axis=(0, 1), keepdims=True)
    denom = float(np.sqrt((a * a).sum()) * np.sqrt((b * b).sum()))
    return -1.0 if denom <= 1e-8 else float((a * b).sum() / denom)


def aligned_ncc(left: np.ndarray, right: np.ndarray, tolerance: int = 2) -> tuple[float, list[int]]:
    """Absorb only the rounding drift introduced by normalized boxes at another scale."""
    if left.shape != right.shape or left.size == 0:
        return -1.0, [0, 0]
    height, width = left.shape[:2]
    best_score, best_shift = -1.0, [0, 0]
    for dy in range(-tolerance, tolerance + 1):
        for dx in range(-tolerance, tolerance + 1):
            left_x1, left_y1 = max(0, dx), max(0, dy)
            left_x2, left_y2 = min(width, width + dx), min(height, height + dy)
            right_x1, right_y1 = max(0, -dx), max(0, -dy)
            right_x2 = right_x1 + (left_x2 - left_x1)
            right_y2 = right_y1 + (left_y2 - left_y1)
            if left_x2 - left_x1 < 4 or left_y2 - left_y1 < 4:
                continue
            score = direct_ncc(
                left[left_y1:left_y2, left_x1:left_x2],
                right[right_y1:right_y2, right_x1:right_x2],
            )
            if score > best_score:
                best_score, best_shift = score, [dx, dy]
    return best_score, best_shift


def control_template_score(panel_class: str, action: str, crop: np.ndarray, control_box: list[int]) -> dict:
    if not CONTROL_TEMPLATE_MANIFEST.exists():
        return {"available": False, "score": -1.0, "threshold": 1.0, "confirmed": False}
    manifest = json.loads(CONTROL_TEMPLATE_MANIFEST.read_text(encoding="utf-8"))
    entry = manifest.get("templates", {}).get(panel_class, {}).get(action)
    if not entry:
        return {"available": False, "score": -1.0, "threshold": 1.0, "confirmed": False}
    template = read_rgb(str(ROOT / entry["path"]))
    x1, y1, x2, y2 = control_box
    candidate = crop[y1:y2, x1:x2]
    if template is None or candidate.size == 0:
        return {"available": False, "score": -1.0, "threshold": float(entry["threshold"]), "confirmed": False}
    if template.shape[:2] != candidate.shape[:2]:
        template = np.asarray(Image.fromarray(template).resize((candidate.shape[1], candidate.shape[0]), Image.Resampling.LANCZOS), dtype=np.uint8)
    score, alignment_shift = aligned_ncc(candidate, template)
    threshold = float(entry["threshold"])
    return {
        "available": True,
        "score": round(score, 4),
        "threshold": threshold,
        "confirmed": score >= threshold,
        "alignment_shift": alignment_shift,
        "path": entry["path"],
    }


def analyze(panel_class: str, crop: np.ndarray, bbox: list[int] | None, ocr_text: str, requested_action: str, item_policy: str, l1_confidence: float) -> dict:
    spec = PANEL_SPECS[panel_class]
    h, w = crop.shape[:2]
    confirmed_ocr, matches = group_matches(ocr_text, spec["keyword_groups"])
    identity_ok = l1_confidence >= 0.60
    controls = {}
    for name, rel in spec["controls"].items():
        box = pixel_box(rel, w, h)
        point = center(box)
        controls[name] = {
            "bbox": box,
            "center": point,
            "screen_bbox": screen_box(box, bbox),
            "screen_center": screen_point(point, bbox),
        }
    ocr_rois = {}
    for name, rel in spec["ocr_rois"].items():
        box = pixel_box(rel, w, h)
        ocr_rois[name] = {"bbox": box, "screen_bbox": screen_box(box, bbox)}

    action_valid = requested_action in spec["safe_actions"]
    requested_control = controls.get(requested_action)
    template_match = control_template_score(panel_class, requested_action, crop, requested_control["bbox"]) if requested_control else {
        "available": False,
        "score": -1.0,
        "threshold": 1.0,
        "confirmed": False,
    }
    template_confirmation_allowed = panel_class in TEMPLATE_CONFIRMABLE
    template_confirmed = template_confirmation_allowed and bool(template_match.get("confirmed"))
    policy_state = "needs_opentext_confirmation"
    if identity_ok and action_valid and (confirmed_ocr or template_confirmed):
        policy_state = "confirmed_candidate"
    if panel_class == "shop_panel" and requested_action == "sell" and item_policy != "allow_sell":
        policy_state = "item_policy_not_allow_sell"
    primary = controls.get(requested_action) if policy_state == "confirmed_candidate" else None
    return {
        "ok": True,
        "slot": panel_class,
        "coordinate_space": "crop_xyxy",
        "input_bbox": bbox,
        "crop_size": [w, h],
        "l1_confidence": round(l1_confidence, 4),
        "identity_state": "matched" if identity_ok else "unknown",
        "features": panel_features(crop),
        "ocr_text_provided": bool(ocr_text.strip()),
        "matched_ocr_keywords": matches,
        "ocr_confirmed": confirmed_ocr,
        "control_template_match": template_match,
        "template_confirmation_allowed": template_confirmation_allowed,
        "confirmation_source": "ocr" if confirmed_ocr else "control_template" if template_confirmed else "none",
        "ocr_rois": ocr_rois,
        "controls": controls,
        "requested_action": requested_action,
        "item_policy": item_policy,
        "policy_state": policy_state,
        "button_candidate_usable": primary is not None,
        "button_candidate_ambiguity": "normal" if primary is not None else "blocked",
        "primary_button_candidate": primary,
        "safety_rule": "A click is exposed only after L1 identity plus OCR or an approved in-panel control template. Selling additionally requires allow_sell; generic confirmation dialogs remain OCR-only.",
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--image", required=True)
    parser.add_argument("--panel-class", required=True, choices=sorted(PANEL_SPECS))
    parser.add_argument("--bbox", type=parse_bbox)
    parser.add_argument("--ocr-text", default="")
    parser.add_argument("--requested-action", default="")
    parser.add_argument("--item-policy", default="unknown", choices=["unknown", "keep", "allow_sell"])
    parser.add_argument("--l1-confidence", type=float, default=1.0)
    args = parser.parse_args()
    image = read_rgb(args.image)
    if image is None:
        result = {"ok": False, "error": "cannot read image"}
    else:
        crop = crop_by_bbox(image, args.bbox)
        result = {"ok": False, "error": "empty crop"} if crop.size == 0 else analyze(args.panel_class, crop, args.bbox, args.ocr_text, args.requested_action, args.item_policy, args.l1_confidence)
    sys.stdout.write(json.dumps(result, ensure_ascii=False))
    return 0 if result.get("ok") else 2


if __name__ == "__main__":
    raise SystemExit(main())
