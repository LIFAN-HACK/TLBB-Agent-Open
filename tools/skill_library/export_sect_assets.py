#!/usr/bin/env python3
"""Export one sect's reviewable asset table, contact sheet, and capture manifest."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

from skill_icon_common import load_catalog


SECT_SLUGS = {
    "少林": "shaolin",
    "明教": "mingjiao",
    "丐帮": "gaibang",
    "武当": "wudang",
    "峨嵋": "emei",
    "星宿": "xingxiu",
    "天龙": "tianlong",
    "天山": "tianshan",
    "逍遥": "xiaoyao",
    "慕容": "murong",
}


def font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    candidates = [
        Path(r"C:\Windows\Fonts\msyhbd.ttc" if bold else r"C:\Windows\Fonts\msyh.ttc"),
        Path(r"C:\Windows\Fonts\simhei.ttf"),
    ]
    for candidate in candidates:
        if candidate.exists():
            return ImageFont.truetype(str(candidate), size)
    return ImageFont.load_default()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--library-root", type=Path, required=True)
    parser.add_argument("--sect", required=True)
    parser.add_argument("--output-dir", type=Path)
    args = parser.parse_args()

    root = args.library_root.resolve()
    records = [record for record in load_catalog(root / "catalog.jsonl") if record.get("sect") == args.sect]
    records.sort(key=lambda record: (int(record.get("source_order", 9999)), record["skill_id"]))
    if not records:
        raise SystemExit(f"sect not found in catalog: {args.sect}")
    slug = SECT_SLUGS.get(args.sect, args.sect)
    output = (args.output_dir or root / "reports" / slug).resolve()
    output.mkdir(parents=True, exist_ok=True)

    columns = [
        "skill_id",
        "name",
        "kind",
        "targeting",
        "release_rule",
        "safety_tags",
        "hang_usable",
        "runtime_match_eligible",
        "calibration_state",
        "icon_raw",
        "source_url",
        "intro",
        "detail",
    ]
    table = []
    for record in records:
        policy = record.get("automation_policy", {})
        calibrated = bool(record.get("icon_ingame_variants"))
        table.append(
            {
                "skill_id": record["skill_id"],
                "name": record["name"],
                "kind": record.get("kind", ""),
                "targeting": policy.get("targeting", "unknown"),
                "release_rule": policy.get("release_rule", policy.get("release_decision", "manual_review_required")),
                "safety_tags": ",".join(policy.get("safety_tags", [])),
                "hang_usable": bool(record.get("hang_usable")),
                "runtime_match_eligible": bool(record.get("runtime_match_eligible")),
                "calibration_state": "calibrated" if calibrated else "official_seed_only",
                "icon_raw": record.get("icon_raw", ""),
                "source_url": record.get("source_url", ""),
                "intro": record.get("intro", ""),
                "detail": record.get("detail", ""),
            }
        )

    with (output / "asset_table.csv").open("w", encoding="utf-8-sig", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=columns)
        writer.writeheader()
        writer.writerows(table)
    (output / "asset_table.json").write_text(json.dumps(table, ensure_ascii=False, indent=2), encoding="utf-8")

    md = [
        f"# {args.sect}技能资产表",
        "",
        f"- 技能数：{len(table)}",
        f"- 官网图标完整：{sum(bool(item['icon_raw']) for item in table)}/{len(table)}",
        f"- 游戏内已校准：{sum(item['calibration_state'] == 'calibrated' for item in table)}/{len(table)}",
        "- 安全边界：`official_seed_only` 只允许输出候选身份，不得自动释放。",
        "",
        "| ID | 技能 | 类型 | 目标 | 释放规则 | 风险标签 | 运行资格 |",
        "|---|---|---|---|---|---|---|",
    ]
    for item in table:
        md.append(
            f"| {item['skill_id']} | {item['name']} | {item['kind']} | {item['targeting']} | "
            f"{item['release_rule']} | {item['safety_tags'] or '-'} | {item['calibration_state']} |"
        )
    (output / "asset_table.md").write_text("\n".join(md) + "\n", encoding="utf-8")

    manifest = {
        "schema": 1,
        "sect": args.sect,
        "purpose": "global_skill_library_bootstrap_for_client_version",
        "safety_policy": "candidate_only_until_reviewed_ingame_templates_exist",
        "capture_requirements": {
            "scope": "one-time shared asset production; never per player or per slot arrangement",
            "client_version": "0.08.1808 or record the actual tested version",
            "states": ["ready_without_cd_overlay", "cooling_with_gray_or_digit_overlay"],
            "minimum_ready_samples_per_visible_skill": 2,
            "minimum_cooling_samples_per_visible_skill": 2,
            "resolutions": ["native", "0.75_scale", "0.50_scale"],
            "crop_source": "L1 skill_bar crop; never use a hand-entered full-screen absolute ROI",
        },
        "skills": [],
    }
    for record in records:
        raw_path = root / record["icon_raw"]
        manifest["skills"].append(
            {
                "skill_id": record["skill_id"],
                "name": record["name"],
                "official_icon": record["icon_raw"],
                "official_sha256": hashlib.sha256(raw_path.read_bytes()).hexdigest(),
                "ingame_variants": record.get("icon_ingame_variants", []),
                "state": "calibrated" if record.get("icon_ingame_variants") else "capture_required",
            }
        )
    (output / "client_version_bootstrap_manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8"
    )

    card_w, card_h, gap, columns_count = 310, 116, 12, 4
    rows_count = math.ceil(len(records) / columns_count)
    width = gap + columns_count * (card_w + gap)
    height = 82 + rows_count * (card_h + gap)
    sheet = Image.new("RGB", (width, height), "#f2f3f5")
    draw = ImageDraw.Draw(sheet)
    draw.text((18, 12), f"{args.sect}技能官网种子资产", fill="#17191c", font=font(26, bold=True))
    draw.text((18, 48), "只用于候选识别与共享库校准，不具备自动释放权限", fill="#a33a2b", font=font(16))
    title_font, body_font, small_font = font(18, bold=True), font(14), font(12)
    for index, record in enumerate(records):
        row, column = divmod(index, columns_count)
        x = gap + column * (card_w + gap)
        y = 76 + row * (card_h + gap)
        draw.rounded_rectangle((x, y, x + card_w, y + card_h), radius=5, fill="#ffffff", outline="#c8cbd0", width=1)
        icon = Image.open(root / record["icon_raw"]).convert("RGB").resize((76, 76), Image.Resampling.NEAREST)
        sheet.paste(icon, (x + 12, y + 20))
        policy = record.get("automation_policy", {})
        draw.text((x + 100, y + 12), f"{record['skill_id']}  {record['name']}", fill="#15171a", font=title_font)
        draw.text((x + 100, y + 43), record.get("kind", "other"), fill="#315c8a", font=body_font)
        draw.text((x + 100, y + 67), str(policy.get("release_rule", "manual_review_required")), fill="#5d6168", font=small_font)
        tags = ", ".join(policy.get("safety_tags", [])) or "no special tag"
        if len(tags) > 34:
            tags = tags[:31] + "..."
        draw.text((x + 100, y + 88), tags, fill="#7a4a22", font=small_font)
    sheet.save(output / "review_sheet.png", optimize=True)

    print(
        json.dumps(
            {
                "sect": args.sect,
                "skills": len(records),
                "output": str(output),
                "files": ["asset_table.csv", "asset_table.json", "asset_table.md", "review_sheet.png", "client_version_bootstrap_manifest.json"],
            },
            ensure_ascii=False,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
