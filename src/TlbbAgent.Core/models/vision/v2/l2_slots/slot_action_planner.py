# -*- coding: utf-8 -*-
"""Convert TLBB L2 slot results into safe click/noop actions.

This planner is intentionally conservative. It never infers intent from a
single visual feature; policy slots must already be OCR-confirmed and expose a
usable primary button.
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path


def load_json_arg(value: str) -> dict:
    path = Path(value)
    if path.exists():
        return json.loads(path.read_text(encoding="utf-8"))
    return json.loads(value)


def noop(reason: str, **extra: object) -> dict:
    result = {"ok": True, "action": "noop", "reason": reason}
    result.update(extra)
    return result


def click(point: list[int], reason: str, **extra: object) -> dict:
    result = {"ok": True, "action": "click", "screen_click_point": point, "reason": reason}
    result.update(extra)
    return result


def first_ready_pet_skill(pet_restore: dict, business_class: str) -> dict | None:
    for item in pet_restore.get("clickable", []):
        if item.get("business_class") == business_class and item.get("identity_state") == "matched" and item.get("cd_state") == "ready":
            return item
    return None


def plan_player_mana(player: dict, pet_restore: dict, mp_threshold: float) -> dict:
    mp_ratio = player.get("mp_ratio")
    if mp_ratio is None:
        return noop("player_mp_ratio_missing")
    if float(mp_ratio) > mp_threshold:
        return noop("player_mp_above_threshold", mp_ratio=mp_ratio, threshold=mp_threshold)
    skill = first_ready_pet_skill(pet_restore, "mana_restore_skill")
    if not skill:
        return noop("no_ready_mana_restore_skill", mp_ratio=mp_ratio, threshold=mp_threshold)
    point = skill.get("screen_click_point") or skill.get("click_point")
    if not point:
        return noop("mana_restore_skill_missing_click_point", skill=skill)
    return click(point, "player_mp_below_threshold_and_mana_restore_ready", mp_ratio=mp_ratio, threshold=mp_threshold, skill=skill)


def plan_player_health(player: dict, pet_restore: dict, hp_threshold: float) -> dict:
    hp_ratio = player.get("hp_ratio")
    if hp_ratio is None:
        return noop("player_hp_ratio_missing")
    if float(hp_ratio) > hp_threshold:
        return noop("player_hp_above_threshold", hp_ratio=hp_ratio, threshold=hp_threshold)
    skill = first_ready_pet_skill(pet_restore, "health_restore_skill")
    if not skill:
        return noop("no_ready_health_restore_skill", hp_ratio=hp_ratio, threshold=hp_threshold)
    point = skill.get("screen_click_point") or skill.get("click_point")
    if not point:
        return noop("health_restore_skill_missing_click_point", skill=skill)
    return click(point, "player_hp_below_threshold_and_health_restore_ready", hp_ratio=hp_ratio, threshold=hp_threshold, skill=skill)


def plan_pet_health(pet: dict, pet_restore: dict, hp_threshold: float) -> dict:
    if pet.get("state") != "deployed":
        return noop("pet_not_deployed_or_unknown", pet_state=pet.get("state"))
    hp_ratio = pet.get("hp_ratio")
    if hp_ratio is None:
        return noop("pet_hp_ratio_missing")
    if float(hp_ratio) > hp_threshold:
        return noop("pet_hp_above_threshold", hp_ratio=hp_ratio, threshold=hp_threshold)
    skill = first_ready_pet_skill(pet_restore, "health_restore_skill")
    if not skill:
        return noop("no_ready_health_restore_skill", hp_ratio=hp_ratio, threshold=hp_threshold)
    point = skill.get("screen_click_point") or skill.get("click_point")
    if not point:
        return noop("health_restore_skill_missing_click_point", skill=skill)
    return click(point, "pet_hp_below_threshold_and_health_restore_ready", hp_ratio=hp_ratio, threshold=hp_threshold, skill=skill)


def plan_hotkey(hotkeys: dict, key: str) -> dict:
    key = key.upper()
    for item in hotkeys.get("slots", []):
        if item.get("key", "").upper() != key:
            continue
        if item.get("cd_state") != "ready":
            return noop("hotkey_not_ready", key=key, cd_state=item.get("cd_state"), slot=item)
        point = item.get("screen_click_point") or item.get("click_point")
        if not point:
            return noop("hotkey_missing_click_point", key=key, slot=item)
        return click(point, "hotkey_ready", key=key, slot=item)
    return noop("hotkey_not_found", key=key)


def plan_policy_slot(slot_result: dict, mode: str) -> dict:
    if slot_result.get("policy_state") != "confirmed_candidate":
        return noop("policy_not_confirmed", mode=mode, policy_state=slot_result.get("policy_state"), matched_ocr_keywords=slot_result.get("matched_ocr_keywords"))
    if slot_result.get("button_candidate_usable") is not True:
        return noop("button_candidate_not_usable", mode=mode, ambiguity=slot_result.get("button_candidate_ambiguity"))
    button = slot_result.get("primary_button_candidate")
    if not button:
        return noop("primary_button_missing", mode=mode)
    point = button.get("screen_center") or button.get("center")
    if not point:
        return noop("primary_button_missing_center", mode=mode, button=button)
    return click(point, f"{mode}_policy_confirmed", mode=mode, button=button, matched_ocr_keywords=slot_result.get("matched_ocr_keywords", []))


def plan_minimap_tracking(minimap: dict) -> dict:
    state = minimap.get("nearby_monster_state")
    if state == "present" or (state is None and minimap.get("nearby_monster_present") is True):
        return {"ok": True, "action": "allow_find_monster_or_attack_loop", "reason": "nearby_monster_present", "nearby_monster_count": minimap.get("nearby_monster_count")}
    if state == "absent" or (state is None and minimap.get("nearby_monster_present") is False):
        temporal_confirmed = minimap.get("decision_scope") == "temporal_fusion" and minimap.get("absence_confirmed") is True
        if temporal_confirmed:
            return {"ok": True, "action": "stop_find_monster_polling", "reason": "temporally_confirmed_monster_absence"}
        return noop("single_frame_minimap_absence_is_weak_evidence")
    return noop("minimap_monster_state_unknown")


def parse_coord_text(text: str) -> tuple[int, int] | None:
    digits: list[str] = []
    current = ""
    for char in text:
        if char.isdigit():
            current += char
        elif current:
            digits.append(current)
            current = ""
    if current:
        digits.append(current)
    if len(digits) < 2:
        return None
    return int(digits[0]), int(digits[1])


def plan_auto_nav_arrival(current_coord_text: str, target_x: int, target_y: int, tolerance: float, current_map: str = "", target_map: str = "") -> dict:
    coord = parse_coord_text(current_coord_text)
    if coord is None:
        return noop("coord_text_parse_failed", coord_text=current_coord_text)
    if target_map and current_map and target_map not in current_map:
        return {"ok": True, "action": "continue_navigation", "reason": "map_not_matched", "current_map": current_map, "target_map": target_map, "current_coord": list(coord)}
    distance = math.dist(coord, (target_x, target_y))
    if distance <= tolerance:
        return {"ok": True, "action": "arrived", "reason": "coord_within_tolerance", "current_coord": list(coord), "target_coord": [target_x, target_y], "distance": round(distance, 3), "tolerance": tolerance}
    return {"ok": True, "action": "continue_navigation", "reason": "coord_not_reached", "current_coord": list(coord), "target_coord": [target_x, target_y], "distance": round(distance, 3), "tolerance": tolerance}


def plan_auto_nav_progress(coord_texts: list[str], target_x: int, target_y: int, tolerance: float, stuck_radius: float, min_samples: int, current_map: str = "", target_map: str = "") -> dict:
    if target_map and current_map and target_map not in current_map:
        return {
            "ok": True,
            "action": "continue_navigation",
            "reason": "map_not_matched",
            "current_map": current_map,
            "target_map": target_map,
        }
    parsed: list[tuple[int, int]] = []
    failed: list[str] = []
    for text in coord_texts:
        coord = parse_coord_text(text)
        if coord is None:
            failed.append(text)
        else:
            parsed.append(coord)
    if not parsed:
        return noop("coord_text_parse_failed", coord_texts=coord_texts)
    current = parsed[-1]
    distance = math.dist(current, (target_x, target_y))
    if distance <= tolerance:
        return {
            "ok": True,
            "action": "arrived",
            "reason": "coord_within_tolerance",
            "current_coord": list(current),
            "target_coord": [target_x, target_y],
            "distance": round(distance, 3),
            "tolerance": tolerance,
            "parsed_count": len(parsed),
            "failed_count": len(failed),
        }
    if len(parsed) >= min_samples:
        xs = [coord[0] for coord in parsed[-min_samples:]]
        ys = [coord[1] for coord in parsed[-min_samples:]]
        spread = math.dist((min(xs), min(ys)), (max(xs), max(ys)))
        if spread <= stuck_radius:
            return {
                "ok": True,
                "action": "maybe_stuck",
                "reason": "coord_not_changing",
                "current_coord": list(current),
                "target_coord": [target_x, target_y],
                "distance": round(distance, 3),
                "sample_spread": round(spread, 3),
                "stuck_radius": stuck_radius,
                "parsed_count": len(parsed),
                "failed_count": len(failed),
            }
    return {
        "ok": True,
        "action": "continue_navigation",
        "reason": "coord_progress_unresolved",
        "current_coord": list(current),
        "target_coord": [target_x, target_y],
        "distance": round(distance, 3),
        "parsed_count": len(parsed),
        "failed_count": len(failed),
    }


def load_policy_list(value: str) -> set[str]:
    if not value:
        return set()
    path = Path(value)
    if path.exists():
        data = json.loads(path.read_text(encoding="utf-8"))
        if isinstance(data, list):
            return {str(item) for item in data}
        if isinstance(data, dict):
            return {str(item) for item in data.get("items", [])}
    return {item.strip() for item in value.split(",") if item.strip()}


def contains_any(text: str, items: set[str]) -> str | None:
    for item in sorted(items, key=len, reverse=True):
        if item and item in text:
            return item
    return None


def plan_shop_item_policy(item_ocr_text: str, keep_items: set[str], sell_items: set[str]) -> dict:
    if not item_ocr_text.strip():
        return noop("item_ocr_text_missing")
    keep_match = contains_any(item_ocr_text, keep_items)
    if keep_match:
        return {"ok": True, "action": "keep_item", "reason": "matched_keep_policy", "matched_item": keep_match, "item_ocr_text": item_ocr_text}
    sell_match = contains_any(item_ocr_text, sell_items)
    if sell_match:
        return {"ok": True, "action": "allow_sell_item", "reason": "matched_sell_policy", "matched_item": sell_match, "item_ocr_text": item_ocr_text}
    return {"ok": True, "action": "keep_item", "reason": "unknown_item_not_sell", "item_ocr_text": item_ocr_text}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="mode", required=True)

    p = sub.add_parser("player_mana")
    p.add_argument("--player", required=True)
    p.add_argument("--pet-restore", required=True)
    p.add_argument("--mp-threshold", type=float, default=0.35)

    p = sub.add_parser("pet_health")
    p.add_argument("--pet", required=True)
    p.add_argument("--pet-restore", required=True)
    p.add_argument("--hp-threshold", type=float, default=0.45)

    p = sub.add_parser("hotkey")
    p.add_argument("--hotkeys", required=True)
    p.add_argument("--key", required=True)

    p = sub.add_parser("policy")
    p.add_argument("--slot-result", required=True)
    p.add_argument("--policy-mode", required=True, choices=["material", "monthly_card", "town_return", "shop_cleanup", "auto_nav"])

    p = sub.add_parser("minimap_tracking")
    p.add_argument("--minimap", required=True)

    p = sub.add_parser("auto_nav_arrival")
    p.add_argument("--coord-text", required=True)
    p.add_argument("--target-x", type=int, required=True)
    p.add_argument("--target-y", type=int, required=True)
    p.add_argument("--tolerance", type=float, default=3.0)
    p.add_argument("--current-map", default="")
    p.add_argument("--target-map", default="")

    p = sub.add_parser("auto_nav_progress")
    p.add_argument("--coord-texts", required=True, help="JSON list or semicolon-separated OCR coord texts")
    p.add_argument("--target-x", type=int, required=True)
    p.add_argument("--target-y", type=int, required=True)
    p.add_argument("--tolerance", type=float, default=3.0)
    p.add_argument("--stuck-radius", type=float, default=1.0)
    p.add_argument("--min-samples", type=int, default=4)
    p.add_argument("--current-map", default="")
    p.add_argument("--target-map", default="")

    p = sub.add_parser("shop_item_policy")
    p.add_argument("--item-ocr-text", required=True)
    p.add_argument("--keep-items", default="")
    p.add_argument("--sell-items", default="")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.mode == "player_mana":
        result = plan_player_mana(load_json_arg(args.player), load_json_arg(args.pet_restore), args.mp_threshold)
    elif args.mode == "pet_health":
        result = plan_pet_health(load_json_arg(args.pet), load_json_arg(args.pet_restore), args.hp_threshold)
    elif args.mode == "hotkey":
        result = plan_hotkey(load_json_arg(args.hotkeys), args.key)
    elif args.mode == "policy":
        result = plan_policy_slot(load_json_arg(args.slot_result), args.policy_mode)
    elif args.mode == "minimap_tracking":
        result = plan_minimap_tracking(load_json_arg(args.minimap))
    elif args.mode == "auto_nav_arrival":
        result = plan_auto_nav_arrival(args.coord_text, args.target_x, args.target_y, args.tolerance, args.current_map, args.target_map)
    elif args.mode == "auto_nav_progress":
        try:
            coord_texts = json.loads(args.coord_texts)
            if not isinstance(coord_texts, list):
                coord_texts = [str(coord_texts)]
            coord_texts = [str(item) for item in coord_texts]
        except Exception:
            coord_texts = [item.strip() for item in args.coord_texts.split(";") if item.strip()]
        result = plan_auto_nav_progress(coord_texts, args.target_x, args.target_y, args.tolerance, args.stuck_radius, args.min_samples, args.current_map, args.target_map)
    else:
        result = plan_shop_item_policy(args.item_ocr_text, load_policy_list(args.keep_items), load_policy_list(args.sell_items))
    sys.stdout.write(json.dumps(result, ensure_ascii=False))
    return 0 if result.get("ok") else 2


if __name__ == "__main__":
    raise SystemExit(main())
