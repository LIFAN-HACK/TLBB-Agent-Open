#!/usr/bin/env python3
"""Build a diagnostics-only skill index from official web icon seeds."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

from PIL import Image

from skill_icon_common import feature_record, load_catalog


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--library-root", type=Path, required=True)
    parser.add_argument("--sect", action="append", default=[], help="Optional candidate sect allowlist")
    parser.add_argument("--high-threshold", type=float, default=0.70)
    parser.add_argument("--low-threshold", type=float, default=0.50)
    parser.add_argument("--margin", type=float, default=0.04)
    args = parser.parse_args()

    root = args.library_root.resolve()
    selected_sects = set(args.sect)
    templates = []
    for record in load_catalog(root / "catalog.jsonl"):
        if selected_sects and record.get("sect") not in selected_sects:
            continue
        relative = str(record.get("icon_raw", ""))
        if record.get("source_kind") != "official_web_seed" or not relative:
            continue
        if not relative.startswith("icons_raw/"):
            raise SystemExit(f"official seed is outside icons_raw: {relative}")
        path = root / relative
        if not path.exists():
            raise SystemExit(f"missing official seed: {path}")
        templates.append(
            {
                "skill_id": record["skill_id"],
                "name": record["name"],
                "sect": record["sect"],
                "kind": record["kind"],
                "path": relative,
                "source_kind": "official_web_seed",
                "auto_release_allowed": False,
                "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                **feature_record(Image.open(path).convert("RGB")),
            }
        )
    if not templates:
        raise SystemExit("no official icons_raw seeds found")

    payload = {
        "schema": 1,
        "candidate_source_policy": "official_web_candidates_only",
        "action_policy": "diagnostics_only_never_cast",
        "calibration_scope": "global_library_per_client_version_not_per_player",
        "normalization": {
            "size": 32,
            "inner_border": 3,
            "features": ["dhash", "rgb_histogram", "rgb_ncc", "gray_ncc"],
        },
        "thresholds": {
            "high": args.high_threshold,
            "low": args.low_threshold,
            "minimum_margin": args.margin,
        },
        "templates": templates,
    }
    output = root / "seed_index" / "index.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    temp = output.with_suffix(".json.tmp")
    temp.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    temp.replace(output)
    print(
        json.dumps(
            {
                "templates": len(templates),
                "skills": len({item["skill_id"] for item in templates}),
                "sects": sorted({item["sect"] for item in templates}),
                "output": str(output),
            },
            ensure_ascii=False,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
