#!/usr/bin/env python3
"""Import manually verified common/item skill-bar identities from a live client review."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from PIL import Image


SOURCE_FILE = "20260709_182648_842_04_\u6280\u80fd_2560x1369_000001.png"
SOURCE_BBOXES = {
    "r1c6": (1283, 1232, 1315, 1264),
    "r1c7": (1319, 1232, 1351, 1264),
    "r1c10": (1427, 1232, 1459, 1264),
    "r2c4": (1211, 1268, 1243, 1300),
    "r3c4": (1211, 1314, 1243, 1346),
    "r3c8": (1355, 1314, 1387, 1346),
}

RECORDS = [
    {
        "skill_id": "common_dart_throw",
        "name": "\u6697\u5668\u6295\u63b7",
        "sect": "\u666e\u901a",
        "kind": "common_attack",
        "hang_usable": False,
        "representative_slot": "r1c6",
        "verified_slots": ["r1c6", "r3c4"],
    },
    {
        "skill_id": "common_summon_mount",
        "name": "\u53ec\u5524\u5750\u9a91",
        "sect": "\u666e\u901a",
        "kind": "common_action",
        "hang_usable": False,
        "representative_slot": "r1c7",
        "verified_slots": ["r1c7"],
    },
    {
        "skill_id": "item_pet_food_variant_001",
        "name": "\u5ba0\u7cae\uff08\u6837\u672c01\uff09",
        "sect": "\u9053\u5177",
        "kind": "item_pet_food",
        "hang_usable": False,
        "representative_slot": "r1c10",
        "verified_slots": ["r1c10"],
    },
    {
        "skill_id": "common_dismount_arrow",
        "name": "\u843d\u9a6c\u7bad",
        "sect": "\u666e\u901a",
        "kind": "common_attack",
        "hang_usable": False,
        "representative_slot": "r2c4",
        "verified_slots": ["r2c4"],
    },
    {
        "skill_id": "common_basic_stealth",
        "name": "\u521d\u7ea7\u9690\u9041",
        "sect": "\u666e\u901a",
        "kind": "common_utility",
        "hang_usable": False,
        "representative_slot": "r3c8",
        "verified_slots": ["r3c8"],
    },
]


def write_catalog(root: Path) -> None:
    path = root / "catalog.jsonl"
    rows = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]
    by_id = {row["skill_id"]: row for row in rows}
    for source in RECORDS:
        skill_id = source["skill_id"]
        by_id[skill_id] = {
            "skill_id": skill_id,
            "name": source["name"],
            "sect": source["sect"],
            "kind": source["kind"],
            "hang_usable": source["hang_usable"],
            "desc": "Live client tooltip verified; policy-disabled by default.",
            "intro": "",
            "detail": "",
            "icon_raw": f"icons_raw/{skill_id}.png",
            "icon_ingame": "",
            "source_url": "",
            "source_icon_url": "",
            "source_kind": "live_client_tooltip_review",
            "runtime_match_eligible": True,
            "version_tags": ["0.08.1808", "live-client", "manual-review"],
            "icon_ingame_variants": [],
        }
    ordered = sorted(by_id.values(), key=lambda row: row["skill_id"])
    path.write_text(
        "\n".join(json.dumps(row, ensure_ascii=False, separators=(",", ":")) for row in ordered) + "\n",
        encoding="utf-8",
    )


def write_raw_icons(root: Path, frame: Image.Image) -> None:
    output = root / "icons_raw"
    output.mkdir(parents=True, exist_ok=True)
    for record in RECORDS:
        slot_id = record["representative_slot"]
        frame.crop(SOURCE_BBOXES[slot_id]).save(output / f"{record['skill_id']}.png")


def write_overrides(root: Path) -> None:
    path = root / "calibration_overrides.json"
    payload = json.loads(path.read_text(encoding="utf-8"))
    entries = [
        row
        for row in payload.get("entries", [])
        if not (row.get("file") == SOURCE_FILE and row.get("slot_id") in SOURCE_BBOXES)
        and not (row.get("file") == SOURCE_FILE and row.get("slot_id") == "r2c2")
    ]
    entries.append(
        {
            "file": SOURCE_FILE,
            "slot_id": "r2c2",
            "exclude": True,
            "reason": "Human review rejected xy_020; exact common/pet identity still pending.",
        }
    )
    for record in RECORDS:
        for slot_id in record["verified_slots"]:
            entries.append(
                {
                    "file": SOURCE_FILE,
                    "slot_id": slot_id,
                    "skill_id": record["skill_id"],
                    "cd_state": "ready",
                    "use_as_template": True,
                    "verification": "live_client_tooltip_or_user_confirmed",
                }
            )
    payload["schema"] = 2
    payload["review_policy"] = (
        "Sect, common, item and pet identities are separated. Excluded slots never become templates."
    )
    payload["entries"] = entries
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--library-root", type=Path, required=True)
    parser.add_argument("--frame", type=Path, required=True)
    args = parser.parse_args()
    root = args.library_root.resolve()
    frame = Image.open(args.frame).convert("RGB")
    write_raw_icons(root, frame)
    write_catalog(root)
    write_overrides(root)
    print(json.dumps({"records": len(RECORDS), "excluded": ["r2c2"]}, ensure_ascii=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
