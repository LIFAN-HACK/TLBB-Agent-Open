# -*- coding: utf-8 -*-
"""
L2 OpenText worker: RapidOCR (PP-OCR ONNX) one-shot CLI for TLBB Agent.

Usage:
  python opentext_worker.py --image path.png --slot target_name --mode auto
  stdout JSON: {"ok":true,"text":"...","confidence":0.7,"engine":"rapidocr_onnxruntime",...}
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import cv2
import numpy as np


def imread_unicode(path: str):
    data = np.fromfile(path, dtype=np.uint8)
    if data.size == 0:
        return None
    return cv2.imdecode(data, cv2.IMREAD_COLOR)


def enhance_variants(img: np.ndarray):
    out = [("color", img)]
    g = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    inv = 255 - g
    invn = cv2.normalize(inv, None, 0, 255, cv2.NORM_MINMAX)
    inv_bgr = cv2.cvtColor(invn, cv2.COLOR_GRAY2BGR)
    out.append(("inv", inv_bgr))
    h, w = img.shape[:2]
    if h < 120:
        out.append(("color_x2", cv2.resize(img, (w * 2, h * 2), interpolation=cv2.INTER_CUBIC)))
        out.append(("inv_x2", cv2.resize(inv_bgr, (w * 2, h * 2), interpolation=cv2.INTER_CUBIC)))
    return out


def rec_only(engine, img: np.ndarray, min_score: float = 0.25):
    rec_res, _ = engine.text_recognizer([img])
    best_t, best_s = "", 0.0
    for text, score in rec_res:
        sc = float(score)
        if text and sc >= min_score and sc > best_s:
            best_t, best_s = str(text), sc
    return best_t, best_s


def det_rec(engine, img: np.ndarray, min_score: float = 0.35):
    try:
        result, _ = engine(img, text_score=min_score, box_thresh=0.35)
    except Exception:
        return "", 0.0
    if not result:
        return "", 0.0
    # join top texts by confidence
    parts = []
    best = 0.0
    for row in result[:8]:
        t = str(row[1]).strip()
        s = float(row[2])
        if t:
            parts.append(t)
            best = max(best, s)
    return "".join(parts) if len(parts) <= 2 else " ".join(parts), best


def create_engine():
    try:
        from rapidocr_onnxruntime import RapidOCR
    except Exception as e:
        return None, f"import failed: {e}"

    engine = RapidOCR()
    engine.use_angle_cls = True
    return engine, ""


def run_with_engine(engine, image_path: str, slot: str, mode: str) -> dict:

    img = imread_unicode(image_path)
    if img is None:
        return {
            "ok": False,
            "text": "",
            "confidence": 0.0,
            "engine": "rapidocr_onnxruntime",
            "error": "cannot read image",
        }

    # name-like slots prefer whole-strip recognition (det fragments glyphs)
    prefer_rec = slot in ("target_name", "map_name", "item_name", "coord_text") or mode == "rec_only"

    best_t, best_s, best_v = "", 0.0, ""
    if mode in ("auto", "rec_only") or prefer_rec:
        for name, im in enhance_variants(img):
            t, s = rec_only(engine, im)
            if s > best_s:
                best_t, best_s, best_v = t, s, name

    if mode in ("auto", "det_rec") and (best_s < 0.45 or not prefer_rec):
        t, s = det_rec(engine, img)
        if s > best_s:
            best_t, best_s, best_v = t, s, "det_rec"

    return {
        "ok": True,
        "text": best_t,
        "confidence": round(best_s, 4),
        "engine": "rapidocr_onnxruntime",
        "variant": best_v,
        "slot": slot,
        "mode": mode,
    }


def run(image_path: str, slot: str, mode: str) -> dict:
    engine, error = create_engine()
    if engine is None:
        return {
            "ok": False,
            "text": "",
            "confidence": 0.0,
            "engine": "rapidocr_onnxruntime",
            "error": error,
        }
    return run_with_engine(engine, image_path, slot, mode)


def write_json_line(value: dict):
    # ASCII JSON avoids Windows code-page ambiguity between Python and .NET.
    sys.stdout.write(json.dumps(value, ensure_ascii=True) + "\n")
    sys.stdout.flush()


def serve() -> int:
    engine, error = create_engine()
    if engine is None:
        write_json_line({"ready": False, "error": error, "engine": "rapidocr_onnxruntime"})
        return 2

    write_json_line({"ready": True, "engine": "rapidocr_onnxruntime"})
    for line in sys.stdin:
        try:
            request = json.loads(line)
            result = run_with_engine(
                engine,
                str(request.get("image", "")),
                str(request.get("slot", "generic")),
                str(request.get("mode", "auto")),
            )
        except Exception as exc:
            result = {
                "ok": False,
                "text": "",
                "confidence": 0.0,
                "engine": "rapidocr_onnxruntime",
                "error": str(exc),
            }
        write_json_line(result)
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--image")
    ap.add_argument("--slot", default="generic")
    ap.add_argument("--mode", default="auto", choices=["auto", "rec_only", "det_rec"])
    ap.add_argument("--serve", action="store_true")
    args = ap.parse_args()
    if args.serve:
        return serve()
    if not args.image:
        ap.error("--image is required unless --serve is used")
    result = run(args.image, args.slot, args.mode)
    sys.stdout.write(json.dumps(result, ensure_ascii=True))
    sys.stdout.flush()
    return 0 if result.get("ok") else 2


if __name__ == "__main__":
    raise SystemExit(main())
