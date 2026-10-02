# -*- coding: utf-8 -*-
"""Runtime-only matcher. The index is required to contain icons_ingame paths."""

from __future__ import annotations

import json
import math
from pathlib import Path

import numpy as np
from PIL import Image


def _inner(image: np.ndarray, mask_key_hint: bool = False) -> np.ndarray:
    value = Image.fromarray(image.astype(np.uint8), "RGB").resize((32, 32), Image.Resampling.LANCZOS)
    inner = np.asarray(value, dtype=np.float32)[3:29, 3:29].copy()
    if mask_key_hint:
        inner[0:11, 18:26] = 0
    return inner


def _gray(rgb: np.ndarray) -> np.ndarray:
    return rgb[:, :, 0] * 0.299 + rgb[:, :, 1] * 0.587 + rgb[:, :, 2] * 0.114


def _ncc(left: np.ndarray, right: np.ndarray) -> float:
    a = left.astype(np.float32) - float(left.mean())
    b = right.astype(np.float32) - float(right.mean())
    denominator = math.sqrt(float((a * a).sum()) * float((b * b).sum()))
    return float((a * b).sum() / denominator) if denominator > 1e-6 else 0.0


def _hist(rgb: np.ndarray) -> np.ndarray:
    values = []
    for channel in range(3):
        current, _ = np.histogram(rgb[:, :, channel], bins=8, range=(0, 256))
        values.extend(current.astype(np.float32))
    output = np.asarray(values, dtype=np.float32)
    norm = float(np.linalg.norm(output))
    return output / norm if norm > 1e-6 else output


def _dhash(rgb: np.ndarray) -> int:
    image = Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8), "RGB").convert("L").resize((9, 8), Image.Resampling.LANCZOS)
    values = np.asarray(image, dtype=np.int16)
    result = 0
    for bit in (values[:, 1:] >= values[:, :-1]).flat:
        result = (result << 1) | int(bit)
    return result


def _features(image: np.ndarray, mask_key_hint: bool = False) -> dict:
    rgb = _inner(image, mask_key_hint=mask_key_hint)
    return {"rgb": rgb, "gray": _gray(rgb), "hist": _hist(rgb), "dhash": _dhash(rgb)}


def _identity(left: dict, right: dict) -> float:
    rgb = max(0.0, _ncc(left["rgb"], right["rgb"]))
    gray = max(0.0, _ncc(left["gray"], right["gray"]))
    histogram = max(0.0, float(np.dot(left["hist"], right["hist"])))
    return rgb * 0.64 + gray * 0.26 + histogram * 0.10


def _empty_score(image: np.ndarray) -> float:
    rgb = _inner(image, mask_key_hint=False)
    gray = _gray(rgb)
    saturation = rgb.max(axis=2) - rgb.min(axis=2)
    edge = (np.abs(np.diff(gray, axis=0)).mean() + np.abs(np.diff(gray, axis=1)).mean()) / 2.0
    if float(gray.std()) < 7.0 or edge < 1.8:
        return 0.98
    visual = min(1.0, float(saturation.mean()) / 38.0) * 0.45 + min(1.0, float(edge) / 25.0) * 0.55
    return max(0.0, min(1.0, 1.0 - visual))


class RuntimeSkillIconMatcher:
    def __init__(self, library_root: Path):
        self.root = library_root
        self.index = json.loads((library_root / "match_index" / "index.json").read_text(encoding="utf-8"))
        if self.index.get("runtime_source_policy") != "icons_ingame_only":
            raise RuntimeError("skill matcher refuses an index that is not icons_ingame_only")
        self.thresholds = self.index["thresholds"]
        self.templates = []
        for item in self.index["templates"]:
            if not item["path"].startswith("icons_ingame/"):
                raise RuntimeError(f"raw/non-calibrated template rejected: {item['path']}")
            template = dict(item)
            template["dhash_value"] = int(item["dhash"], 16)
            template["hist_value"] = np.asarray(item["hist"], dtype=np.float32)
            template["dhash_masked_value"] = int(item["dhash_masked"], 16)
            template["hist_masked_value"] = np.asarray(item["hist_masked"], dtype=np.float32)
            source = np.asarray(Image.open(library_root / item["path"]).convert("RGB"), dtype=np.uint8)
            template["visual"] = _features(source, mask_key_hint=False)
            template["visual_masked"] = _features(source, mask_key_hint=True)
            self.templates.append(template)

    def match(self, image: np.ndarray, mask_key_hint: bool = False) -> dict:
        vacancy = _empty_score(image)
        if vacancy >= float(self.thresholds["empty"]):
            return {
                "skill_id": "",
                "name": "",
                "score": 0.0,
                "margin": 0.0,
                "status": "empty",
                "identity_source": "none",
                "hang_usable": False,
                "policy_class": "",
                "release_rule": "empty",
            }
        feature = _features(image, mask_key_hint=mask_key_hint)
        dhash_key = "dhash_masked_value" if mask_key_hint else "dhash_value"
        hist_key = "hist_masked_value" if mask_key_hint else "hist_value"
        visual_key = "visual_masked" if mask_key_hint else "visual"
        shortlist = []
        for template in self.templates:
            hash_score = 1.0 - (feature["dhash"] ^ template[dhash_key]).bit_count() / 64.0
            hist_score = float(np.dot(feature["hist"], template[hist_key]))
            shortlist.append((hash_score * 0.55 + hist_score * 0.45, template))
        shortlist.sort(key=lambda item: item[0], reverse=True)
        by_skill = {}
        for _, template in shortlist[: min(18, len(shortlist))]:
            score = _identity(feature, template[visual_key])
            current = by_skill.get(template["skill_id"])
            if current is None or score > current[0]:
                by_skill[template["skill_id"]] = (score, template)
        ranked = sorted(by_skill.values(), key=lambda item: item[0], reverse=True)
        if not ranked:
            return {
                "skill_id": "",
                "name": "",
                "score": 0.0,
                "margin": 0.0,
                "status": "unknown",
                "identity_source": "none",
                "hang_usable": False,
                "policy_class": "",
                "release_rule": "identity_unknown",
            }
        score, best = ranked[0]
        second = ranked[1][0] if len(ranked) > 1 else 0.0
        margin = score - second
        status = "matched" if score >= float(self.thresholds["high"]) and margin >= float(self.thresholds["minimum_margin"]) else "unknown"
        return {
            "skill_id": best["skill_id"] if score >= float(self.thresholds["low"]) else "",
            "name": best["name"] if score >= float(self.thresholds["low"]) else "",
            "score": round(score, 5),
            "margin": round(margin, 5),
            "status": status,
            "identity_source": "icons_ingame",
            "hang_usable": bool(best["hang_usable"]) if status == "matched" else False,
            "policy_class": best.get("kind", ""),
            "release_rule": (
                "allowed_by_policy"
                if status == "matched" and bool(best["hang_usable"])
                else "blocked_by_policy"
            ),
            "template_cd_state": best["cd_state"],
        }
