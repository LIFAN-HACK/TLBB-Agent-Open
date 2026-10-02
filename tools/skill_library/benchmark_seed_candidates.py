#!/usr/bin/env python3
"""Measure official seed candidates against labeled in-game icon variants."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from PIL import Image

from skill_icon_common import identity_score, load_catalog, visual_features


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--library-root", type=Path, required=True)
    parser.add_argument("--calibration-manifest", type=Path)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()

    root = args.library_root.resolve()
    index = json.loads((root / "seed_index" / "index.json").read_text(encoding="utf-8"))
    thresholds = index["thresholds"]
    records = load_catalog(root / "catalog.jsonl")
    manifest_path = args.calibration_manifest or root / "reports" / "calibration_manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    slot_by_path = {item["path"]: item.get("slot_id", "") for item in manifest.get("templates", [])}

    cache: dict[tuple[str, bool], dict] = {}

    def features(relative: str, masked: bool) -> dict:
        key = (relative, masked)
        if key not in cache:
            cache[key] = visual_features(Image.open(root / relative).convert("RGB"), mask_key_hint=masked)
        return cache[key]

    indexed_skill_ids = {template["skill_id"] for template in index["templates"]}
    seeds = [
        record
        for record in records
        if record.get("skill_id") in indexed_skill_ids
        and record.get("source_kind") == "official_web_seed"
        and record.get("icon_raw")
    ]
    rows = []
    for record in records:
        for relative in record.get("icon_ingame_variants", []):
            if not relative.startswith("icons_ingame/") or not record.get("skill_id", "").split("_", 1)[0] in {
                seed["skill_id"].split("_", 1)[0] for seed in seeds
            }:
                continue
            masked = slot_by_path.get(relative, "").startswith("r3")
            query = features(relative, masked)
            ranked = sorted(
                (
                    identity_score(query, features(seed["icon_raw"], masked)),
                    seed["skill_id"],
                    seed["name"],
                )
                for seed in seeds
            )
            ranked.reverse()
            best, second = ranked[0], ranked[1]
            correct = best[1] == record["skill_id"]
            margin = best[0] - second[0]
            confirmed = best[0] >= float(thresholds["high"]) and margin >= float(thresholds["minimum_margin"])
            rows.append(
                {
                    "path": relative,
                    "masked_hotkey": masked,
                    "expected_skill_id": record["skill_id"],
                    "candidate_skill_id": best[1],
                    "candidate_name": best[2],
                    "score": round(best[0], 5),
                    "margin": round(margin, 5),
                    "top1_correct": correct,
                    "candidate_confirmed": confirmed,
                }
            )

    confirmed = [row for row in rows if row["candidate_confirmed"]]
    metrics = {
        "official_seed_candidates": len(seeds),
        "ingame_queries": len(rows),
        "top1_accuracy": round(sum(row["top1_correct"] for row in rows) / max(1, len(rows)), 6),
        "confirmed_candidate_count": len(confirmed),
        "confirmed_correct_count": sum(row["top1_correct"] for row in confirmed),
        "confirmed_false_count": sum(not row["top1_correct"] for row in confirmed),
        "confirmed_correct_coverage": round(
            sum(row["top1_correct"] for row in confirmed) / max(1, len(rows)), 6
        ),
        "thresholds": thresholds,
        "action_policy": index["action_policy"],
    }
    report = args.report or root / "reports" / "seed_candidate_bench.md"
    report.parent.mkdir(parents=True, exist_ok=True)
    report.with_suffix(".json").write_text(
        json.dumps({"metrics": metrics, "rows": rows}, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    report.write_text(
        "\n".join(
            [
                "# 官网技能候选层盲测",
                "",
                f"- 官网候选：{metrics['official_seed_candidates']}",
                f"- 游戏内查询：{metrics['ingame_queries']}",
                f"- Top-1：{metrics['top1_accuracy']:.2%}",
                f"- 达候选阈值且正确：{metrics['confirmed_correct_count']}",
                f"- 达候选阈值但错误：{metrics['confirmed_false_count']}",
                f"- 安全候选覆盖：{metrics['confirmed_correct_coverage']:.2%}",
                "- 候选层永不参与自动释放；只有 icons_ingame 校准层可进入 Matched。",
            ]
        )
        + "\n",
        encoding="utf-8",
    )
    print(json.dumps(metrics, ensure_ascii=False))
    return 0 if metrics["confirmed_false_count"] == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
