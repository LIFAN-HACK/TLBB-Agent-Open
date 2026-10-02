#!/usr/bin/env python3
"""Build the runtime skill-icon index from calibrated in-game templates only."""

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
    parser.add_argument("--high-threshold", type=float, default=0.80)
    parser.add_argument("--low-threshold", type=float, default=0.62)
    parser.add_argument("--margin", type=float, default=0.04)
    args = parser.parse_args()

    root = args.library_root.resolve()
    templates = []
    for record in load_catalog(root / "catalog.jsonl"):
        for relative in record.get("icon_ingame_variants", []):
            if not relative.startswith("icons_ingame/"):
                raise SystemExit(f"runtime template is outside icons_ingame: {relative}")
            path = root / relative
            if not path.exists():
                raise SystemExit(f"missing calibrated template: {path}")
            name = path.stem
            cd_state = "cooling" if "_cooling_" in name else "ready" if "_ready_" in name else "unknown"
            templates.append(
                {
                    "skill_id": record["skill_id"],
                    "name": record["name"],
                    "sect": record["sect"],
                    "kind": record["kind"],
                    "hang_usable": bool(record["hang_usable"]),
                    "path": relative,
                    "cd_state": cd_state,
                    "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                    **feature_record(Image.open(path).convert("RGB")),
                }
            )
    if not templates:
        raise SystemExit("no calibrated icons_ingame templates found")

    payload = {
        "schema": 1,
        "runtime_source_policy": "icons_ingame_only",
        "normalization": {"size": 32, "inner_border": 3, "features": ["dhash", "rgb_histogram", "rgb_ncc", "gray_ncc"]},
        "thresholds": {
            "high": args.high_threshold,
            "low": args.low_threshold,
            "minimum_margin": args.margin,
            "empty": 0.92,
        },
        "templates": templates,
    }
    output = root / "match_index" / "index.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    temp = output.with_suffix(".json.tmp")
    temp.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    temp.replace(output)
    print(json.dumps({"templates": len(templates), "skills": len({item['skill_id'] for item in templates}), "output": str(output)}, ensure_ascii=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
