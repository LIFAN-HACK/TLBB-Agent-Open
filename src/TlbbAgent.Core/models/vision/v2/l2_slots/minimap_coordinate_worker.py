from __future__ import annotations

import argparse
import csv
import io
import json
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np
from PIL import Image


COORD_ROI = [0.43, 0.16, 0.73, 0.22]
MAP_NAME_ROI = [0.1875, 0.06578947368421052, 0.625, 0.15350877192982457]
CANONICAL_SIZE = [240, 228]
DIGIT_TEMPLATE_MANIFEST = Path(__file__).resolve().parent / "templates" / "minimap_digits" / "manifest.json"
_DIGIT_TEMPLATE_CACHE: tuple[dict[str, list[np.ndarray]], float] | None = None


def read_rgb(path: str) -> np.ndarray | None:
    try:
        return np.asarray(Image.open(path).convert("RGB"), dtype=np.uint8)
    except Exception:
        return None


def parse_bbox(value: str) -> list[int]:
    parts = [round(float(item.strip())) for item in value.split(",")]
    if len(parts) != 4:
        raise argparse.ArgumentTypeError("bbox must be x1,y1,x2,y2")
    return parts


def crop_by_bbox(image: np.ndarray, bbox: list[int] | None) -> np.ndarray:
    if bbox is None:
        return image
    height, width = image.shape[:2]
    x1, y1, x2, y2 = bbox
    x1, x2 = max(0, min(width, x1)), max(0, min(width, x2))
    y1, y2 = max(0, min(height, y1)), max(0, min(height, y2))
    return image[y1:y2, x1:x2] if x2 > x1 and y2 > y1 else image[0:0, 0:0]


def pixel_box(relative: list[float], width: int, height: int) -> list[int]:
    return [round(relative[0] * width), round(relative[1] * height), round(relative[2] * width), round(relative[3] * height)]


def screen_box(box: list[int], bbox: list[int] | None) -> list[int]:
    if bbox is None:
        return box
    return [bbox[0] + box[0], bbox[1] + box[1], bbox[0] + box[2], bbox[1] + box[3]]


def components(mask: np.ndarray) -> list[dict]:
    seen = np.zeros(mask.shape, dtype=bool)
    result: list[dict] = []
    height, width = mask.shape
    for start_y, start_x in zip(*np.where(mask)):
        if seen[start_y, start_x]:
            continue
        seen[start_y, start_x] = True
        stack = [(int(start_y), int(start_x))]
        points: list[tuple[int, int]] = []
        while stack:
            y, x = stack.pop()
            points.append((y, x))
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    ny, nx = y + dy, x + dx
                    if 0 <= ny < height and 0 <= nx < width and mask[ny, nx] and not seen[ny, nx]:
                        seen[ny, nx] = True
                        stack.append((ny, nx))
        ys = [point[0] for point in points]
        xs = [point[1] for point in points]
        result.append({"area": len(points), "bbox": [min(xs), min(ys), max(xs) + 1, max(ys) + 1]})
    return result


def make_digit_mask(crop: np.ndarray) -> np.ndarray:
    red, green, blue = crop[:, :, 0], crop[:, :, 1], crop[:, :, 2]
    return (red > 200) & (green > 170) & (blue < 120)


def prepare_ocr_image(mask: np.ndarray) -> Image.Image | None:
    ys, xs = np.where(mask)
    if len(xs) == 0:
        return None
    x1, x2 = max(0, int(xs.min()) - 1), min(mask.shape[1], int(xs.max()) + 2)
    y1, y2 = max(0, int(ys.min()) - 1), min(mask.shape[0], int(ys.max()) + 2)
    binary = np.where(mask[y1:y2, x1:x2], 0, 255).astype(np.uint8)
    glyph_image = Image.fromarray(binary, mode="L")
    # Pixel-font digits need a small quiet zone. Without it Tesseract clips 6/8
    # against the word boundary even when the glyph segmentation is correct.
    image = Image.new("L", (glyph_image.width + 6, glyph_image.height + 6), 255)
    image.paste(glyph_image, (3, 3))
    return image.resize((max(24, image.width * 12), max(24, image.height * 12)), Image.Resampling.NEAREST)


def tesseract_digits(image: Image.Image) -> tuple[str, float, str]:
    executable = shutil.which("tesseract")
    if not executable:
        return "", 0.0, "tesseract executable not found"
    with tempfile.TemporaryDirectory(prefix="tlbb_coord_") as temp_dir:
        image_path = Path(temp_dir) / "coord.png"
        image.save(image_path)
        command = [
            executable,
            str(image_path),
            "stdout",
            "-l",
            "chi_sim",
            "--psm",
            "8",
            "-c",
            "tessedit_char_whitelist=0123456789",
            "tsv",
        ]
        completed = subprocess.run(command, text=True, encoding="utf-8", errors="replace", capture_output=True, check=False)
    if completed.returncode != 0:
        return "", 0.0, completed.stderr.strip()[-400:]
    texts: list[str] = []
    scores: list[float] = []
    for row in csv.DictReader(io.StringIO(completed.stdout), delimiter="\t"):
        value = re.sub(r"\D", "", row.get("text") or "")
        if not value:
            continue
        texts.append(value)
        try:
            score = float(row.get("conf") or 0.0)
        except ValueError:
            score = 0.0
        scores.append(max(0.0, score) / 100.0)
    return "".join(texts), max(scores, default=0.0), ""


def classify_pixel_digits(mask: np.ndarray, glyphs: list[dict]) -> tuple[str, list[float], float]:
    global _DIGIT_TEMPLATE_CACHE
    if not DIGIT_TEMPLATE_MANIFEST.exists() or not glyphs:
        return "", [], 1.0
    if _DIGIT_TEMPLATE_CACHE is None:
        manifest = json.loads(DIGIT_TEMPLATE_MANIFEST.read_text(encoding="utf-8"))
        templates: dict[str, list[np.ndarray]] = {}
        for digit, names in manifest.get("digits", {}).items():
            digit_templates = []
            for name in names:
                path = DIGIT_TEMPLATE_MANIFEST.parent / name
                try:
                    digit_templates.append(np.asarray(Image.open(path).convert("L"), dtype=np.uint8) > 127)
                except Exception:
                    continue
            if digit_templates:
                templates[str(digit)] = digit_templates
        _DIGIT_TEMPLATE_CACHE = (templates, float(manifest.get("classification_threshold", 0.82)))
    templates, threshold = _DIGIT_TEMPLATE_CACHE
    if not templates:
        return "", [], 1.0

    output: list[str] = []
    scores: list[float] = []
    for glyph in sorted(glyphs, key=lambda item: item["bbox"][0]):
        x1, y1, x2, y2 = glyph["bbox"]
        observed = mask[y1:y2, x1:x2]
        best_digit, best_score = "", -1.0
        for digit, digit_templates in templates.items():
            for template in digit_templates:
                resized = np.asarray(
                    Image.fromarray(np.where(observed, 255, 0).astype(np.uint8), mode="L").resize(
                        (template.shape[1], template.shape[0]), Image.Resampling.NEAREST
                    ),
                    dtype=np.uint8,
                ) > 127
                denominator = int(resized.sum()) + int(template.sum())
                score = 0.0 if denominator == 0 else 2.0 * int((resized & template).sum()) / denominator
                if score > best_score:
                    best_digit, best_score = digit, score
        output.append(best_digit)
        scores.append(round(best_score, 4))
    return "".join(output), scores, threshold


def split_digits(digits: str, glyphs: list[dict], roi_width: int) -> tuple[str, str, dict]:
    glyphs = sorted(glyphs, key=lambda item: item["bbox"][0])
    gaps = [glyphs[index + 1]["bbox"][0] - glyphs[index]["bbox"][2] for index in range(len(glyphs) - 1)]
    split_index = None
    if len(glyphs) == len(digits) and 2 <= len(glyphs) <= 6 and gaps:
        split_index = int(np.argmax(gaps)) + 1
    elif len(digits) == 6:
        split_index = 3
    elif 2 <= len(digits) <= 6 and glyphs:
        fixed_split = roi_width * 0.5
        inferred = sum((item["bbox"][0] + item["bbox"][2]) / 2 < fixed_split for item in glyphs)
        if 1 <= inferred < len(digits):
            split_index = inferred
    if split_index is None or not (1 <= split_index < len(digits)):
        return "", "", {"split_index": None, "gaps": gaps, "reason": "cannot split coordinate groups"}
    return digits[:split_index], digits[split_index:], {"split_index": split_index, "gaps": gaps, "reason": "largest glyph gap"}


def analyze(minimap: np.ndarray, input_bbox: list[int] | None) -> dict:
    height, width = minimap.shape[:2]
    if [width, height] == CANONICAL_SIZE:
        analysis = minimap
    else:
        analysis = np.asarray(
            Image.fromarray(minimap).resize(tuple(CANONICAL_SIZE), Image.Resampling.LANCZOS),
            dtype=np.uint8,
        )
    analysis_height, analysis_width = analysis.shape[:2]
    coord_box = pixel_box(COORD_ROI, width, height)
    map_box = pixel_box(MAP_NAME_ROI, width, height)
    analysis_coord_box = pixel_box(COORD_ROI, analysis_width, analysis_height)
    x1, y1, x2, y2 = analysis_coord_box
    coord_crop = analysis[y1:y2, x1:x2]
    if coord_crop.size == 0:
        return {"ok": False, "error": "empty coordinate ROI"}
    mask = make_digit_mask(coord_crop)
    minimum_height = max(4, round(coord_crop.shape[0] * 0.35))
    glyphs = [
        item
        for item in components(mask)
        if item["area"] >= 3 and item["bbox"][3] - item["bbox"][1] >= minimum_height
    ]
    template_digits, template_scores, template_threshold = classify_pixel_digits(mask, glyphs)
    template_x, template_y, template_split = split_digits(template_digits, glyphs, coord_crop.shape[1])
    template_valid = bool(
        template_x
        and template_y
        and len(template_x) <= 3
        and len(template_y) <= 3
        and template_scores
        and min(template_scores) >= template_threshold
    )
    tesseract_raw, raw_confidence, error = "", 0.0, ""
    if template_valid:
        digits, x_text, y_text, split = template_digits, template_x, template_y, template_split
        recognition_engine = "pixel_template"
    else:
        ocr_image = prepare_ocr_image(mask)
        if ocr_image is None:
            error = "no bright coordinate digits"
        else:
            tesseract_raw, raw_confidence, error = tesseract_digits(ocr_image)
        digits = tesseract_raw
        x_text, y_text, split = split_digits(digits, glyphs, coord_crop.shape[1])
        recognition_engine = "tesseract_fallback"
    valid = bool(x_text and y_text and len(x_text) <= 3 and len(y_text) <= 3)
    x_value, y_value = (int(x_text), int(y_text)) if valid else (None, None)
    if valid and not (0 <= x_value <= 999 and 0 <= y_value <= 999):
        valid, x_value, y_value = False, None, None
    structural_match = len(glyphs) == len(digits) and valid
    return {
        "ok": True,
        "slot": "minimap_coordinate",
        "coordinate_space": "crop_xyxy",
        "input_bbox": input_bbox,
        "crop_size": [width, height],
        "analysis_size": [analysis_width, analysis_height],
        "coord_text_roi_normalized": COORD_ROI,
        "coord_text_roi": coord_box,
        "coord_text_analysis_roi": analysis_coord_box,
        "coord_text_screen_roi": screen_box(coord_box, input_bbox),
        "map_name_roi_normalized": MAP_NAME_ROI,
        "map_name_roi": map_box,
        "map_name_screen_roi": screen_box(map_box, input_bbox),
        "raw_digits": digits,
        "recognition_engine": recognition_engine,
        "pixel_template_digits": template_digits,
        "pixel_template_scores": template_scores,
        "pixel_template_threshold": template_threshold,
        "raw_tesseract_digits": tesseract_raw,
        "raw_tesseract_confidence": round(raw_confidence, 4),
        "glyph_count": len(glyphs),
        "glyphs": glyphs,
        "split_diagnostics": split,
        "coordinate": None if not valid else {"x": x_value, "y": y_value, "text": f"{x_value},{y_value}"},
        "state": "confirmed_candidate" if structural_match else "unknown",
        "structural_match": structural_match,
        "error": error,
        "safety_rule": "Coordinates are exposed only when OCR digit count equals the detected pixel-font glyph count and both groups are 1-3 digits.",
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--image", required=True)
    parser.add_argument("--bbox", type=parse_bbox, help="L1 minimap screen bbox; omit when image is already the minimap crop")
    args = parser.parse_args()
    image = read_rgb(args.image)
    if image is None:
        result = {"ok": False, "error": "cannot read image"}
    else:
        minimap = crop_by_bbox(image, args.bbox)
        result = {"ok": False, "error": "empty minimap crop"} if minimap.size == 0 else analyze(minimap, args.bbox)
    sys.stdout.write(json.dumps(result, ensure_ascii=False))
    return 0 if result.get("ok") else 2


if __name__ == "__main__":
    raise SystemExit(main())
