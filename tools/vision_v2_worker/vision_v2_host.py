# -*- coding: utf-8 -*-
"""Persistent host for the TLBB Vision V2 slot artifacts.

The host performs perception and action planning only. It never sends input or
clicks a game window. Each stdin line is one JSON request; each stdout line is
one JSON response with the same request id.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import sys
import traceback
from pathlib import Path

import numpy as np


def load_module(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"cannot load module: {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def json_default(value):
    if isinstance(value, np.generic):
        return value.item()
    if isinstance(value, np.ndarray):
        return value.tolist()
    raise TypeError(f"not JSON serializable: {type(value).__name__}")


class VisionRuntime:
    def __init__(self, artifacts_root: Path):
        self.root = artifacts_root
        self.slots = load_module("tlbb_v2_slots", artifacts_root / "l2_slots" / "l2_slot_worker.py")
        self.skill_runtime = load_module(
            "tlbb_v2_skill_runtime", artifacts_root / "l2_slots" / "skill_icon_runtime.py"
        )
        skill_library_root = artifacts_root / "skill_library"
        self.skill_matcher = (
            self.skill_runtime.RuntimeSkillIconMatcher(skill_library_root)
            if (skill_library_root / "match_index" / "index.json").exists()
            else None
        )
        self.coords = load_module(
            "tlbb_v2_coords", artifacts_root / "l2_slots" / "minimap_coordinate_worker.py"
        )
        self.fixed = load_module(
            "tlbb_v2_fixed", artifacts_root / "fixed_ui_slots" / "fixed_ui_slot_worker.py"
        )
        self.aux = load_module("tlbb_v2_aux", artifacts_root / "l1_ui_aux" / "ui_aux_locator.py")
        self.panels = load_module(
            "tlbb_v2_panels", artifacts_root / "l2_ui_panels" / "ui_panel_slot_worker.py"
        )
        self.planner = load_module(
            "tlbb_v2_planner", artifacts_root / "l2_slots" / "slot_action_planner.py"
        )

        self.fixed_config = json.loads(
            (artifacts_root / "fixed_ui_slots" / "inference_config.json").read_text(encoding="utf-8")
        )
        self.fixed_templates = self.fixed.load_templates(self.fixed_config)
        self.aux_config = json.loads(
            (artifacts_root / "l1_ui_aux" / "locator_config.json").read_text(encoding="utf-8")
        )
        self.aux_templates = {}
        for class_name, class_spec in self.aux_config["classes"].items():
            templates = []
            for relative in class_spec.get("templates", []):
                image = self.aux.read_rgb(str(artifacts_root / "l1_ui_aux" / relative))
                if image is not None:
                    templates.append((relative, self.aux.gray(image)))
            self.aux_templates[class_name] = templates

    def dispatch(self, request: dict) -> dict:
        op = request.get("op", "")
        if op == "probe":
            return {
                "ok": True,
                "engine": "tlbb_vision_v2_host",
                "artifacts_root": str(self.root),
                "slot_modes": ["player", "pet", "hotkeys", "skill_bar_slots", "dynamic_skill_slots", "minimap"],
                "skill_matcher": "ready" if self.skill_matcher is not None else "unavailable",
            }
        if op == "slot":
            return self.slot(request)
        if op == "minimap_coordinate":
            return self.minimap_coordinate(request)
        if op == "fixed_pet_restore":
            return self.fixed_pet_restore(request)
        if op == "aux":
            return self.aux_locate(request)
        if op == "panel":
            return self.panel(request)
        if op == "plan":
            return self.plan(request)
        return {"ok": False, "error": f"unknown op: {op}"}

    def slot(self, request: dict) -> dict:
        image = self.slots.read_rgb(str(request.get("image", "")))
        if image is None:
            return {"ok": False, "error": "cannot read slot image"}
        mode = request.get("mode", "")
        if mode == "skill_bar_slots":
            result = self.slots.skill_bar_slots(image, self.skill_matcher)
            bbox = request.get("bbox")
            result["coordinate_space"] = "crop_xyxy"
            result["input_bbox"] = bbox
            return self.slots.enrich_screen_coordinates(result, bbox)
        if mode == "dynamic_skill_slots":
            result = self.slots.dynamic_skill_slots(
                image,
                self.skill_matcher,
                request.get("slots", []),
                str(request.get("surface", "")),
            )
            bbox = request.get("bbox")
            result["coordinate_space"] = "crop_xyxy"
            result["input_bbox"] = bbox
            return self.slots.enrich_screen_coordinates(result, bbox)
        handlers = {
            "player": self.slots.player_status,
            "pet": self.slots.pet_status,
            "hotkeys": self.slots.hotkeys,
            "minimap": self.slots.minimap,
        }
        handler = handlers.get(mode)
        if handler is None:
            return {"ok": False, "error": f"unsupported slot mode: {mode}"}
        result = handler(image)
        bbox = request.get("bbox")
        result["coordinate_space"] = "crop_xyxy"
        result["input_bbox"] = bbox
        return self.slots.enrich_screen_coordinates(result, bbox)

    def minimap_coordinate(self, request: dict) -> dict:
        image = self.coords.read_rgb(str(request.get("image", "")))
        if image is None:
            return {"ok": False, "error": "cannot read minimap image"}
        return self.coords.analyze(image, request.get("bbox"))

    def fixed_pet_restore(self, request: dict) -> dict:
        image = self.fixed.imread_unicode(str(request.get("image", "")))
        if image is None:
            return {"ok": False, "error": "cannot read skill bar image"}
        result = self.fixed.recognize_pet_restore(image, self.fixed_config, self.fixed_templates)
        bbox = request.get("bbox")
        result["coordinate_space"] = "crop_xyxy"
        result["input_bbox"] = bbox
        return self.fixed.enrich_screen_coordinates(result, bbox)

    def aux_locate(self, request: dict) -> dict:
        frame = self.aux.read_rgb(str(request.get("image", "")))
        if frame is None:
            return {"ok": False, "error": "cannot read full frame"}
        allow = {str(item) for item in request.get("classes", []) if str(item)}
        frame_gray = self.aux.gray(frame)
        detections = []
        for class_name, spec in self.aux_config["classes"].items():
            if allow and class_name not in allow:
                continue
            best = None
            best_template = ""
            for relative, template_gray in self.aux_templates.get(class_name, []):
                located = self.aux.locate_one(
                    frame_gray,
                    template_gray,
                    spec,
                    float(self.aux_config.get("search_downsample", 0.5)),
                )
                if located and (best is None or located["confidence"] > best["confidence"]):
                    best = located
                    best_template = relative
            if best:
                best["class_name"] = class_name
                best["template"] = best_template
                detections.append(best)
        detections.sort(key=lambda item: item["confidence"], reverse=True)
        return {
            "ok": True,
            "engine": "l1_ui_aux_template_locator_v1",
            "coordinate_space": "client_xyxy",
            "image_size": [int(frame.shape[1]), int(frame.shape[0])],
            "detections": detections,
            "class_count": len(detections),
            "contract": self.aux_config["contract"],
        }

    def panel(self, request: dict) -> dict:
        crop = self.panels.read_rgb(str(request.get("image", "")))
        if crop is None:
            return {"ok": False, "error": "cannot read panel crop"}
        panel_class = str(request.get("panel_class", ""))
        if panel_class not in self.panels.PANEL_SPECS:
            return {"ok": False, "error": f"unsupported panel class: {panel_class}"}
        return self.panels.analyze(
            panel_class,
            crop,
            request.get("bbox"),
            str(request.get("ocr_text", "")),
            str(request.get("requested_action", "")),
            str(request.get("item_policy", "unknown")),
            float(request.get("l1_confidence", 1.0)),
        )

    def plan(self, request: dict) -> dict:
        mode = str(request.get("mode", ""))
        payload = request.get("payload") or {}
        if mode == "player_mana":
            return self.planner.plan_player_mana(
                payload.get("player", {}),
                payload.get("pet_restore", {}),
                float(payload.get("mp_threshold", 0.35)),
            )
        if mode == "player_health":
            return self.planner.plan_player_health(
                payload.get("player", {}),
                payload.get("pet_restore", {}),
                float(payload.get("hp_threshold", 0.45)),
            )
        if mode == "pet_health":
            return self.planner.plan_pet_health(
                payload.get("pet", {}),
                payload.get("pet_restore", {}),
                float(payload.get("hp_threshold", 0.45)),
            )
        if mode == "hotkey":
            return self.planner.plan_hotkey(payload.get("hotkeys", {}), str(payload.get("key", "")))
        if mode == "policy":
            return self.planner.plan_policy_slot(payload.get("slot_result", {}), str(payload.get("policy_mode", "")))
        if mode == "minimap_tracking":
            return self.planner.plan_minimap_tracking(payload.get("minimap", {}))
        if mode == "auto_nav_arrival":
            return self.planner.plan_auto_nav_arrival(
                str(payload.get("coord_text", "")),
                int(payload.get("target_x", 0)),
                int(payload.get("target_y", 0)),
                float(payload.get("tolerance", 3.0)),
                str(payload.get("current_map", "")),
                str(payload.get("target_map", "")),
            )
        if mode == "auto_nav_progress":
            return self.planner.plan_auto_nav_progress(
                [str(item) for item in payload.get("coord_texts", [])],
                int(payload.get("target_x", 0)),
                int(payload.get("target_y", 0)),
                float(payload.get("tolerance", 3.0)),
                float(payload.get("stuck_radius", 1.0)),
                int(payload.get("min_samples", 4)),
                str(payload.get("current_map", "")),
                str(payload.get("target_map", "")),
            )
        if mode == "shop_item_policy":
            keep = {str(item) for item in payload.get("keep_items", [])}
            sell = {str(item) for item in payload.get("sell_items", [])}
            return self.planner.plan_shop_item_policy(str(payload.get("item_ocr_text", "")), keep, sell)
        return self.planner.noop("unsupported_plan_mode", mode=mode)


def run_server(runtime: VisionRuntime) -> int:
    if hasattr(sys.stdin, "reconfigure"):
        sys.stdin.reconfigure(encoding="utf-8", errors="replace")
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="strict")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8", errors="backslashreplace")
    print(
        json.dumps(
            {"ready": True, "engine": "tlbb_vision_v2_host", "artifacts_root": str(runtime.root)},
            ensure_ascii=True,
        ),
        flush=True,
    )
    for line in sys.stdin:
        # .NET/PowerShell may emit a UTF-8 BOM on the first redirected stdin
        # write. Strip it so the first slot request is not lost.
        line = line.lstrip("\ufeff").strip()
        if not line:
            continue
        request_id = None
        try:
            request = json.loads(line)
            request_id = request.get("id")
            result = runtime.dispatch(request)
            response = {"id": request_id, "result": result}
        except Exception as exc:
            traceback.print_exc(file=sys.stderr)
            response = {"id": request_id, "result": {"ok": False, "error": str(exc)}}
        print(json.dumps(response, ensure_ascii=True, default=json_default), flush=True)
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--serve", action="store_true")
    parser.add_argument("--artifacts-root", required=True)
    args = parser.parse_args()
    runtime = VisionRuntime(Path(args.artifacts_root).resolve())
    if args.serve:
        return run_server(runtime)
    print(json.dumps(runtime.dispatch({"op": "probe"}), ensure_ascii=True, default=json_default))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
