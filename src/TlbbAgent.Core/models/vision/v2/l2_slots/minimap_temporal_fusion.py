from __future__ import annotations

import argparse
import json
from collections import deque


class MinimapTemporalFusion:
    """Fuse blinking minimap markers with target-acquisition evidence.

    A yellow marker is strong positive evidence. Marker absence is weak evidence
    because markers blink and can be clipped or covered near the minimap edge.
    """

    def __init__(self, window_ms: int = 1200, minimum_absent_frames: int = 5, minimum_absent_span_ms: int = 800):
        self.window_ms = window_ms
        self.minimum_absent_frames = minimum_absent_frames
        self.minimum_absent_span_ms = minimum_absent_span_ms
        self.frames: deque[dict] = deque()

    def update(
        self,
        timestamp_ms: int,
        frame_result: dict,
        target_panel_visible: bool = False,
        target_search_miss_count: int = 0,
        world_monster_visible: bool | None = None,
    ) -> dict:
        state = frame_result.get("frame_marker_state") or frame_result.get("nearby_monster_state") or "unknown"
        self.frames.append({"timestamp_ms": int(timestamp_ms), "state": state})
        cutoff = int(timestamp_ms) - self.window_ms
        while self.frames and self.frames[0]["timestamp_ms"] < cutoff:
            self.frames.popleft()

        states = [item["state"] for item in self.frames]
        present_frames = sum(value == "present" for value in states)
        absent_frames = sum(value == "absent" for value in states)
        span_ms = 0 if len(self.frames) < 2 else self.frames[-1]["timestamp_ms"] - self.frames[0]["timestamp_ms"]

        strong_positive = target_panel_visible or world_monster_visible is True or present_frames > 0
        temporal_absence = (
            len(states) >= self.minimum_absent_frames
            and absent_frames == len(states)
            and span_ms >= self.minimum_absent_span_ms
        )
        acquisition_absence = target_search_miss_count >= 3

        if strong_positive:
            fused_state = "present"
            reason = (
                "target_panel_visible" if target_panel_visible else
                "world_monster_visible" if world_monster_visible is True else
                "marker_present_within_temporal_window"
            )
        elif temporal_absence and acquisition_absence:
            fused_state = "absent"
            reason = "temporal_marker_absence_and_repeated_target_search_miss"
        else:
            fused_state = "unknown"
            reason = "single_or_unconfirmed_marker_absence"

        return {
            "ok": True,
            "slot": "minimap_temporal_fusion",
            "decision_scope": "temporal_fusion",
            "nearby_monster_state": fused_state,
            "nearby_monster_present": True if fused_state == "present" else False if fused_state == "absent" else None,
            "absence_confirmed": fused_state == "absent",
            "reason": reason,
            "evidence": {
                "window_ms": self.window_ms,
                "sample_count": len(states),
                "span_ms": span_ms,
                "present_frames": present_frames,
                "absent_frames": absent_frames,
                "target_panel_visible": target_panel_visible,
                "target_search_miss_count": target_search_miss_count,
                "world_monster_visible": world_monster_visible,
            },
        }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--samples-json", required=True, help="JSON list containing timestamp_ms and result")
    parser.add_argument("--target-panel-visible", action="store_true")
    parser.add_argument("--target-search-miss-count", type=int, default=0)
    parser.add_argument("--world-monster-visible", choices=["true", "false", "unknown"], default="unknown")
    args = parser.parse_args()
    samples = json.loads(args.samples_json)
    fusion = MinimapTemporalFusion()
    result = None
    world_visible = True if args.world_monster_visible == "true" else False if args.world_monster_visible == "false" else None
    for sample in samples:
        result = fusion.update(
            timestamp_ms=int(sample["timestamp_ms"]),
            frame_result=sample["result"],
            target_panel_visible=args.target_panel_visible,
            target_search_miss_count=args.target_search_miss_count,
            world_monster_visible=world_visible,
        )
    print(json.dumps(result or {"ok": False, "reason": "no_samples"}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
