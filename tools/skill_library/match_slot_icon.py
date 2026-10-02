#!/usr/bin/env python3
"""Match one slot or benchmark a calibrated in-game skill icon index."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

import numpy as np
from PIL import Image

from skill_icon_common import (
    empty_score,
    hamming_distance,
    histogram_similarity,
    identity_score,
    visual_features,
)


class SkillIconMatcher:
    def __init__(self, library_root: Path) -> None:
        self.root = library_root.resolve()
        self.index = json.loads((self.root / "match_index" / "index.json").read_text(encoding="utf-8"))
        if self.index.get("runtime_source_policy") != "icons_ingame_only":
            raise ValueError("skill index violates icons_ingame_only policy")
        self.thresholds = self.index["thresholds"]
        self.templates = []
        for item in self.index["templates"]:
            if not item["path"].startswith("icons_ingame/"):
                raise ValueError(f"runtime template outside icons_ingame: {item['path']}")
            runtime = dict(item)
            runtime["dhash_value"] = int(item["dhash"], 16)
            runtime["hist_value"] = np.asarray(item["hist"], dtype=np.float32)
            runtime["dhash_masked_value"] = int(item["dhash_masked"], 16)
            runtime["hist_masked_value"] = np.asarray(item["hist_masked"], dtype=np.float32)
            template_image = Image.open(self.root / item["path"]).convert("RGB")
            runtime["visual"] = visual_features(template_image, mask_key_hint=False)
            runtime["visual_masked"] = visual_features(template_image, mask_key_hint=True)
            self.templates.append(runtime)

    def match(self, image: Image.Image, mask_key_hint: bool = False) -> dict[str, Any]:
        vacancy = empty_score(image)
        if vacancy >= float(self.thresholds["empty"]):
            return {
                "skill_id": "",
                "name": "",
                "score": 0.0,
                "margin": 0.0,
                "status": "empty",
                "hang_usable": False,
                "template_cd_state": "unknown",
                "empty_score": round(vacancy, 5),
            }

        feature = visual_features(image, mask_key_hint=mask_key_hint)
        dhash_key = "dhash_masked_value" if mask_key_hint else "dhash_value"
        hist_key = "hist_masked_value" if mask_key_hint else "hist_value"
        visual_key = "visual_masked" if mask_key_hint else "visual"
        shortlist = []
        for template in self.templates:
            hash_score = 1.0 - hamming_distance(feature["dhash"], template[dhash_key]) / 64.0
            hist_score = histogram_similarity(feature["hist"], template[hist_key])
            shortlist.append((hash_score * 0.55 + hist_score * 0.45, template))
        shortlist.sort(key=lambda item: item[0], reverse=True)

        by_skill: dict[str, tuple[float, dict[str, Any]]] = {}
        for _, template in shortlist[: min(18, len(shortlist))]:
            score = identity_score(feature, template[visual_key])
            current = by_skill.get(template["skill_id"])
            if current is None or score > current[0]:
                by_skill[template["skill_id"]] = (score, template)
        ranked = sorted(by_skill.values(), key=lambda item: item[0], reverse=True)
        if not ranked:
            return {"skill_id": "", "name": "", "score": 0.0, "margin": 0.0, "status": "unknown", "hang_usable": False}
        best_score, best = ranked[0]
        second_score = ranked[1][0] if len(ranked) > 1 else 0.0
        margin = best_score - second_score
        high = float(self.thresholds["high"])
        low = float(self.thresholds["low"])
        minimum_margin = float(self.thresholds["minimum_margin"])
        status = "matched" if best_score >= high and margin >= minimum_margin else "unknown"
        return {
            "skill_id": best["skill_id"] if best_score >= low else "",
            "name": best["name"] if best_score >= low else "",
            "score": round(best_score, 5),
            "margin": round(margin, 5),
            "status": status,
            "hang_usable": bool(best["hang_usable"]) if status == "matched" else False,
            "template_cd_state": best["cd_state"],
            "template_path": best["path"],
            "empty_score": round(vacancy, 5),
        }


def run_bench(matcher: SkillIconMatcher, manifest_path: Path, report_path: Path) -> int:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    matched_rows = []
    for item in manifest["matched"]:
        result = matcher.match(
            Image.open(matcher.root / item["path"]).convert("RGB"),
            mask_key_hint=bool(item.get("mask_key_hint")),
        )
        matched_rows.append({**item, "result": result, "top1_correct": result.get("skill_id") == item["expected_skill_id"]})
    unknown_rows = []
    for item in manifest["unknown"]:
        result = matcher.match(
            Image.open(matcher.root / item["path"]).convert("RGB"),
            mask_key_hint=bool(item.get("mask_key_hint")),
        )
        unknown_rows.append({**item, "result": result, "false_match": result.get("status") == "matched"})

    top1 = sum(item["top1_correct"] for item in matched_rows) / max(1, len(matched_rows))
    confirmed = sum(item["result"].get("status") == "matched" and item["top1_correct"] for item in matched_rows)
    confirmed_accuracy = confirmed / max(1, len(matched_rows))
    false_unknown = sum(item["false_match"] for item in unknown_rows)
    metrics = {
        "matched_samples": len(matched_rows),
        "top1_accuracy": round(top1, 6),
        "confirmed_correct_rate": round(confirmed_accuracy, 6),
        "unknown_samples": len(unknown_rows),
        "unknown_false_matches": false_unknown,
        "pass_top1_95": top1 >= 0.95,
        "pass_unknown_zero_false_match": false_unknown == 0,
    }
    detail_path = report_path.with_suffix(".json")
    detail_path.write_text(json.dumps({"metrics": metrics, "matched": matched_rows, "unknown": unknown_rows}, ensure_ascii=False, indent=2), encoding="utf-8")
    lines = [
        "# 技能图标匹配基准",
        "",
        f"- 游戏内 matched 留出样本：{metrics['matched_samples']}",
        f"- Top1：{metrics['top1_accuracy']:.2%}",
        f"- 达阈值且正确：{metrics['confirmed_correct_rate']:.2%}",
        f"- unknown/其他职业/空槽样本：{metrics['unknown_samples']}",
        f"- unknown 错配：{metrics['unknown_false_matches']}",
        f"- Top1 ≥95%：{'PASS' if metrics['pass_top1_95'] else 'FAIL'}",
        f"- unknown 零错配：{'PASS' if metrics['pass_unknown_zero_false_match'] else 'FAIL'}",
        "",
        "运行时索引只包含 `icons_ingame/`，官网 `icons_raw/` 未进入匹配路径。",
    ]
    report_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(json.dumps(metrics, ensure_ascii=True))
    return 0 if metrics["pass_top1_95"] and metrics["pass_unknown_zero_false_match"] else 1


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--library-root", type=Path, required=True)
    parser.add_argument("--image", type=Path)
    parser.add_argument("--bench", type=Path)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    if bool(args.image) == bool(args.bench):
        raise SystemExit("provide exactly one of --image or --bench")
    matcher = SkillIconMatcher(args.library_root)
    if args.image:
        print(json.dumps(matcher.match(Image.open(args.image).convert("RGB")), ensure_ascii=False))
        return 0
    report = args.report or args.library_root / "reports" / "match_bench.md"
    return run_bench(matcher, args.bench, report)


if __name__ == "__main__":
    raise SystemExit(main())
