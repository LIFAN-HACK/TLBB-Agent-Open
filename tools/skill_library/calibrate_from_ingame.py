#!/usr/bin/env python3
"""Calibrate official skill seeds against in-game skill-bar captures."""

from __future__ import annotations

import argparse
import json
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any

from PIL import Image, ImageDraw

from skill_icon_common import (
    cooldown_features,
    difference_hash,
    empty_score,
    extract_slots,
    identity_score,
    load_catalog,
    load_skill_crop,
    parse_bbox,
    visual_features,
    write_catalog,
)


RAW_ACCEPT_SCORE = 0.68
RAW_ACCEPT_MARGIN = 0.055
FRAME_MIN_MATCHES = 7


def raw_candidates(
    image: Image.Image,
    raw_features: dict[str, dict[str, dict[str, Any]]],
    mask_key_hint: bool,
) -> list[tuple[float, str]]:
    feature = visual_features(image, mask_key_hint=mask_key_hint)
    key = "masked" if mask_key_hint else "full"
    values = [(identity_score(feature, reference[key]), skill_id) for skill_id, reference in raw_features.items()]
    values.sort(reverse=True)
    return values


def choose_diverse(items: list[dict[str, Any]], limit: int) -> list[dict[str, Any]]:
    selected: list[dict[str, Any]] = []
    hashes: list[int] = []
    for item in sorted(
        items,
        key=lambda value: (bool(value.get("manual_override")), value["raw_score"]),
        reverse=True,
    ):
        current = difference_hash(visual_features(item["image"], mask_key_hint=item["mask_key_hint"])["rgb"])
        if hashes and min((current ^ previous).bit_count() for previous in hashes) < 3:
            continue
        selected.append(item)
        hashes.append(current)
        if len(selected) >= limit:
            break
    if not selected and items:
        selected.append(max(items, key=lambda value: value["raw_score"]))
    return selected


def make_review(root: Path, report_dir: Path, templates: list[dict[str, Any]]) -> None:
    if not templates:
        return
    cards = []
    for item in templates:
        image = Image.open(root / item["path"]).convert("RGB").resize((128, 128), Image.Resampling.NEAREST)
        card = Image.new("RGB", (200, 158), "white")
        card.paste(image, (36, 4))
        ImageDraw.Draw(card).text((5, 136), f"{item['skill_id']} {item['cd_state']} {item['score']:.3f}", fill="black")
        cards.append(card)
    columns = 5
    rows = (len(cards) + columns - 1) // columns
    sheet = Image.new("RGB", (columns * 200, rows * 158), (215, 215, 215))
    for index, card in enumerate(cards):
        sheet.paste(card, ((index % columns) * 200, (index // columns) * 158))
    report_dir.mkdir(parents=True, exist_ok=True)
    sheet.save(report_dir / "calibration_review.png")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", type=Path, required=True, help="Directory containing full frames or L1 crops")
    parser.add_argument("--library-root", type=Path, required=True)
    parser.add_argument("--sect", required=True, help="Target sect to calibrate; required to prevent cross-sect writes")
    parser.add_argument("--bbox", type=str, default="", help="Full-frame skill_bar bbox x1,y1,x2,y2")
    parser.add_argument("--glob", default="*.png")
    parser.add_argument("--max-templates-per-state", type=int, default=3)
    parser.add_argument("--holdout-mod", type=int, default=5)
    parser.add_argument("--overrides", type=Path)
    args = parser.parse_args()

    root = args.library_root.resolve()
    target_sect = args.sect.strip()
    report_slug = {
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
    }.get(target_sect, target_sect)
    report_dir = root / "reports" / "calibration" / report_slug
    report_dir.mkdir(parents=True, exist_ok=True)
    overrides_path = args.overrides or root / "calibration_overrides" / f"{report_slug}.json"
    override_entries = (
        json.loads(overrides_path.read_text(encoding="utf-8")).get("entries", [])
        if overrides_path.exists()
        else []
    )
    overrides = {(item["file"], item["slot_id"]): item for item in override_entries}
    bbox = parse_bbox(args.bbox) if args.bbox else None
    catalog_path = root / "catalog.jsonl"
    records = load_catalog(catalog_path)
    selected_records = [record for record in records if record.get("sect") == target_sect]
    if not selected_records:
        raise SystemExit(f"catalog has no records for requested sect: {target_sect}")
    raw_features: dict[str, dict[str, dict[str, Any]]] = {}
    global_features: dict[str, list[dict[str, dict[str, Any]]]] = defaultdict(list)
    by_id = {record["skill_id"]: record for record in records}
    for record in records:
        raw_path = record.get("icon_raw", "")
        if raw_path and record.get("sect") == target_sect:
            raw_image = Image.open(root / raw_path).convert("RGB")
            raw_features[record["skill_id"]] = {
                "full": visual_features(raw_image, mask_key_hint=False),
                "masked": visual_features(raw_image, mask_key_hint=True),
            }
        if raw_path and record.get("sect") != target_sect:
            raw_image = Image.open(root / raw_path).convert("RGB")
            global_features[record["skill_id"]].append(
                {
                    "full": visual_features(raw_image, mask_key_hint=False),
                    "masked": visual_features(raw_image, mask_key_hint=True),
                }
            )
        if record.get("sect") != target_sect:
            for relative in record.get("icon_ingame_variants", []):
                variant_path = root / relative
                if not variant_path.exists():
                    continue
                variant = Image.open(variant_path).convert("RGB")
                global_features[record["skill_id"]].append(
                    {
                        "full": visual_features(variant, mask_key_hint=False),
                        "masked": visual_features(variant, mask_key_hint=True),
                    }
                )
    if not raw_features:
        raise SystemExit(f"catalog has no raw icon seeds for requested sect: {target_sect}")

    files = sorted(args.input.glob(args.glob))
    if not files:
        raise SystemExit("no input images found")

    frame_rows: list[dict[str, Any]] = []
    for frame_index, path in enumerate(files):
        crop = load_skill_crop(path, bbox)
        analyzed = []
        matched_count = 0
        for slot in extract_slots(crop):
            mask_key_hint = slot["row"] == 2
            candidates = raw_candidates(slot["image"], raw_features, mask_key_hint)
            best_score, best_id = candidates[0]
            second_score = candidates[1][0] if len(candidates) > 1 else 0.0
            feature_key = "masked" if mask_key_hint else "full"
            slot_feature = visual_features(slot["image"], mask_key_hint=mask_key_hint)
            global_candidate = max(
                (
                    max(identity_score(slot_feature, variant[feature_key]) for variant in variants),
                    skill_id,
                )
                for skill_id, variants in global_features.items()
                if variants
            ) if global_features else (0.0, "")
            margin = best_score - second_score
            accepted = best_score >= RAW_ACCEPT_SCORE and margin >= RAW_ACCEPT_MARGIN and empty_score(slot["image"]) < 0.88
            if accepted:
                matched_count += 1
            analyzed.append(
                {
                    **slot,
                    "raw_skill_id": best_id,
                    "raw_score": best_score,
                    "raw_margin": margin,
                    "raw_accepted": accepted,
                    "global_score": global_candidate[0],
                    "global_skill_id": global_candidate[1],
                    "cd": cooldown_features(slot["image"]),
                    "mask_key_hint": mask_key_hint,
                }
            )
        frame_rows.append(
            {
                "index": frame_index,
                "path": path,
                "recognized_sect": matched_count >= FRAME_MIN_MATCHES,
                "match_count": matched_count,
                "slots": analyzed,
            }
        )

    cluster_id = -1
    previous_accepted = -2
    cluster_votes: dict[tuple[int, str], Counter[str]] = defaultdict(Counter)
    for frame in frame_rows:
        if not frame["recognized_sect"]:
            frame["cluster_id"] = None
            continue
        if frame["index"] != previous_accepted + 1:
            cluster_id += 1
        previous_accepted = frame["index"]
        frame["cluster_id"] = cluster_id
        for slot in frame["slots"]:
            if slot["raw_accepted"]:
                cluster_votes[(cluster_id, slot["slot_id"])][slot["raw_skill_id"]] += 1
    cluster_identity = {}
    for key, votes in cluster_votes.items():
        label, count = votes.most_common(1)[0]
        if count >= 2 and count / sum(votes.values()) >= 0.75:
            cluster_identity[key] = label

    template_candidates: dict[tuple[str, str], list[dict[str, Any]]] = defaultdict(list)
    matched_bench: list[dict[str, Any]] = []
    unknown_bench: list[dict[str, Any]] = []
    bench_root = report_dir / "bench_crops"
    for category in ("matched", "unknown"):
        (bench_root / category).mkdir(parents=True, exist_ok=True)

    accepted_frames = 0
    rejected_frames = 0
    for frame in frame_rows:
        frame_has_overrides = any(
            (frame["path"].name, slot["slot_id"]) in overrides for slot in frame["slots"]
        )
        frame_has_global_identity = any(
            slot["global_score"] >= 0.92
            for slot in frame["slots"]
        )
        if frame["recognized_sect"] or frame_has_overrides or frame_has_global_identity:
            if frame["recognized_sect"]:
                accepted_frames += 1
            holdout = frame["index"] % max(2, args.holdout_mod) == 0
            for slot in frame["slots"]:
                override = overrides.get((frame["path"].name, slot["slot_id"]))
                if override and override.get("exclude"):
                    continue
                global_label = (
                    slot["global_skill_id"]
                    if slot["global_score"] >= 0.92
                    else ""
                )
                raw_label = (
                    slot["raw_skill_id"]
                    if slot["raw_accepted"]
                    and frame["recognized_sect"]
                    else ""
                )
                label = override["skill_id"] if override else (
                    raw_label
                    or global_label
                    or (
                        cluster_identity.get((frame["cluster_id"], slot["slot_id"]), "")
                        if frame["recognized_sect"]
                        else ""
                    )
                )
                if (
                    not label
                    or by_id.get(label, {}).get("sect") != target_sect
                    or empty_score(slot["image"]) >= 0.88
                ):
                    continue
                feature_key = "masked" if slot["mask_key_hint"] else "full"
                assigned_score = identity_score(
                    visual_features(slot["image"], mask_key_hint=slot["mask_key_hint"]),
                    raw_features[label][feature_key],
                )
                item = {
                    "skill_id": label,
                    "slot_id": slot["slot_id"],
                    "source": frame["path"].name,
                    "raw_score": assigned_score,
                    "image": slot["image"],
                    "cd_state": override.get("cd_state", slot["cd"]["state"]) if override else slot["cd"]["state"],
                    "mask_key_hint": slot["mask_key_hint"],
                    "manual_override": bool(override),
                }
                force_template = bool(override and override.get("use_as_template"))
                if holdout and (slot["raw_accepted"] or bool(global_label)) and not force_template:
                    output = bench_root / "matched" / f"{frame['index']:04d}_{slot['slot_id']}_{label}.png"
                    slot["image"].save(output)
                    matched_bench.append(
                        {
                            "path": output.relative_to(root).as_posix(),
                            "expected_skill_id": label,
                            "mask_key_hint": slot["mask_key_hint"],
                        }
                    )
                elif (
                    (not holdout or force_template)
                    and item["cd_state"] in {"ready", "cooling"}
                    and (assigned_score >= 0.45 or force_template)
                ):
                    template_candidates[(label, item["cd_state"])].append(item)
        elif frame["match_count"] == 0:
            rejected_frames += 1
            if frame["index"] % 8 == 0:
                for slot in frame["slots"]:
                    if empty_score(slot["image"]) < 0.80:
                        output = bench_root / "unknown" / f"{frame['index']:04d}_{slot['slot_id']}.png"
                        slot["image"].save(output)
                        unknown_bench.append(
                            {
                                "path": output.relative_to(root).as_posix(),
                                "expected_skill_id": "",
                                "mask_key_hint": slot["mask_key_hint"],
                            }
                        )
                        if len(unknown_bench) >= 100:
                            break

    ingame_dir = root / "icons_ingame"
    ingame_dir.mkdir(parents=True, exist_ok=True)
    selected_existing_variants = {
        relative
        for record in selected_records
        for relative in record.get("icon_ingame_variants", [])
        if str(relative).startswith("icons_ingame/")
    }
    manual_candidate_count = sum(
        bool(item.get("manual_override")) for items in template_candidates.values() for item in items
    )
    template_manifest: list[dict[str, Any]] = []
    variants_by_skill: dict[str, list[str]] = defaultdict(list)
    for (skill_id, cd_state), items in sorted(template_candidates.items()):
        for variant_index, item in enumerate(choose_diverse(items, args.max_templates_per_state), start=1):
            output = ingame_dir / f"{skill_id}_{cd_state}_{variant_index:02d}.png"
            item["image"].save(output)
            relative = output.relative_to(root).as_posix()
            variants_by_skill[skill_id].append(relative)
            template_manifest.append(
                {
                    "skill_id": skill_id,
                    "name": by_id[skill_id]["name"],
                    "cd_state": cd_state,
                    "path": relative,
                    "score": round(float(item["raw_score"]), 5),
                    "slot_id": item["slot_id"],
                    "source": item["source"],
                    "manual_override": item.get("manual_override", False),
                }
            )

    if not template_manifest:
        print(
            json.dumps(
                {
                    "sect": target_sect,
                    "input_count": len(files),
                    "accepted_target_frames": accepted_frames,
                    "error": "no reviewed templates were produced; existing runtime assets were left untouched",
                },
                ensure_ascii=False,
            )
        )
        return 1

    selected_new_variants = {item["path"] for item in template_manifest}
    for relative in selected_existing_variants - selected_new_variants:
        old = root / relative
        if old.exists():
            old.unlink()

    for record in selected_records:
        variants = variants_by_skill.get(record["skill_id"], [])
        ready = next((path for path in variants if "_ready_" in path), variants[0] if variants else "")
        record["icon_ingame"] = ready
        record["icon_ingame_variants"] = variants
        record["runtime_match_eligible"] = bool(variants)
    write_catalog(catalog_path, records)

    bench = {
        "schema": 1,
        "matched": matched_bench,
        "unknown": unknown_bench,
        "holdout_rule": f"accepted frame index modulo {max(2, args.holdout_mod)} equals zero",
    }
    (report_dir / "bench_manifest.json").write_text(json.dumps(bench, ensure_ascii=False, indent=2), encoding="utf-8")
    manifest = {
        "schema": 1,
        "sect": target_sect,
        "input": str(args.input.resolve()),
        "input_count": len(files),
        "skill_bbox": list(bbox) if bbox else None,
        "accepted_target_frames": accepted_frames,
        "rejected_other_or_unknown_frames": rejected_frames,
        "raw_accept_score": RAW_ACCEPT_SCORE,
        "raw_accept_margin": RAW_ACCEPT_MARGIN,
        "templates": template_manifest,
        "matched_holdout": len(matched_bench),
        "unknown_holdout": len(unknown_bench),
        "calibration_overrides": str(overrides_path.resolve()) if overrides_path.exists() else "",
        "calibration_override_count": len(override_entries),
        "manual_template_candidate_count": manual_candidate_count,
        "manual_template_selected_count": sum(bool(item.get("manual_override")) for item in template_manifest),
        "frame_diagnostics": [
            {
                "index": frame["index"],
                "file": frame["path"].name,
                "raw_match_count": frame["match_count"],
                "recognized_target_sect": frame["recognized_sect"],
            }
            for frame in frame_rows
        ],
    }
    (report_dir / "calibration_manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    make_review(root, report_dir, template_manifest)
    print(json.dumps({key: manifest[key] for key in ("sect", "input_count", "accepted_target_frames", "rejected_other_or_unknown_frames", "matched_holdout", "unknown_holdout")}, ensure_ascii=False))
    return 0 if template_manifest else 1


if __name__ == "__main__":
    raise SystemExit(main())
