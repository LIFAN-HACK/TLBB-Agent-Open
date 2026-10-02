# -*- coding: utf-8 -*-
"""Classify real UU BMP templates into usable category folders + catalog."""
from __future__ import annotations

import csv
import json
import os
import re
import shutil
from collections import Counter, defaultdict
from pathlib import Path

from PIL import Image

REPO_ROOT = Path(__file__).resolve().parents[1]
SRC = REPO_ROOT / "data" / "imported" / "templates"
OUT = REPO_ROOT / "data" / "imported" / "templates-usable"
VISION_DOCS = Path(
    os.environ.get(
        "TLBB_VISION_DOCS",
        str(Path.home() / "Desktop" / "TLBB 视觉模型" / "docs"),
    )
)

CATEGORIES = {
    "01_对话与选项": "对话框选项、任务对话文案",
    "02_确认取消按钮": "确定/取消/关闭/capie 等",
    "03_技能与技能栏": "技能图标、技能界面、显示技能栏",
    "04_进度条": "捕兽/通用进度条",
    "05_背包与物品": "背包格物品、药品、材料图标",
    "06_地图与传送": "地图名图块、世界地图、寻路",
    "07_任务栏与追踪": "任务栏、任务追踪、可接完成",
    "08_NPC与角色": "NPC、主人等相关",
    "09_血条与状态UI": "血条框、二行血、血上限等",
    "10_宠物宝宝": "出战/休息等",
    "11_怪物相关": "怪物头像/死亡/任务怪名",
    "12_主线剧情": "主线相关图",
    "13_副本": "副本相关",
    "14_仓库交易商店": "仓库、交易、商店",
    "15_好友组队社交": "好友等",
    "16_系统提示": "系统信息、提示",
    "17_生活采集": "挖矿采药锄头钓鱼相关",
    "18_其他UI控件": "滑块、查找、杂项 UI",
    "19_生活王编码图块": "生活王无前缀编码名(BT/QQ/R/C等)，用途需对照原模块",
    "20_生活王无前缀杂项": "生活王_无前缀下未能语义归类的明文图",
    "21_挖矿无前缀杂项": "挖矿包无前缀未语义归类",
    "99_未分类": "其它未能自动归类",
}

RULES: list[tuple[str, list[str]]] = [
    ("04_进度条", [r"进度条", r"进度"]),
    ("02_确认取消按钮", [r"capie_", r"确定", r"取消", r"关闭", r"确认"]),
    ("01_对话与选项", [r"对话_", r"\\UI\\"]),
    ("03_技能与技能栏", [r"技能", r"普攻", r"轻功", r"显示技能"]),
    ("17_生活采集", [r"锄头", r"采药", r"钓鱼", r"挖矿", r"采集", r"矿石", r"白药"]),
    ("05_背包与物品", [r"背包", r"血药", r"物品_", r"粮食"]),
    ("06_地图与传送", [r"地图", r"世界地图", r"自动寻路", r"寻路", r"传送"]),
    ("07_任务栏与追踪", [r"任务栏", r"任务追踪", r"可接", r"任务完成", r"师门任务"]),
    ("10_宠物宝宝", [r"宝宝", r"宠物", r"出战", r"休息"]),
    ("11_怪物相关", [r"怪物", r"贪官", r"叛徒"]),
    ("08_NPC与角色", [r"NPC", r"npc", r"主人"]),
    ("09_血条与状态UI", [r"血条", r"二行血", r"血上限", r"队员血"]),
    ("12_主线剧情", [r"主线"]),
    ("13_副本", [r"副本"]),
    ("14_仓库交易商店", [r"仓库", r"交易", r"商店", r"摊位"]),
    ("15_好友组队社交", [r"好友", r"组队", r"队伍"]),
    ("16_系统提示", [r"系统信息", r"提示_"]),
    ("18_其他UI控件", [r"滑块", r"查找", r"显示_"]),
]

PARENT_MAP = {
    "背包": "05_背包与物品",
    "地图": "06_地图与传送",
    "世界地图": "06_地图与传送",
    "自动寻路": "06_地图与传送",
    "UI": "01_对话与选项",
    "任务栏": "07_任务栏与追踪",
    "任务追踪": "07_任务栏与追踪",
    "技能": "03_技能与技能栏",
    "进度条": "04_进度条",
    "NPC": "08_NPC与角色",
    "宝宝": "10_宠物宝宝",
    "怪物": "11_怪物相关",
    "主线": "12_主线剧情",
    "副本": "13_副本",
    "仓库": "14_仓库交易商店",
    "交易": "14_仓库交易商店",
    "商店": "14_仓库交易商店",
    "好友": "15_好友组队社交",
    "系统信息": "16_系统提示",
    "拾取": "05_背包与物品",
    "查找": "18_其他UI控件",
    "快": "18_其他UI控件",
    "世": "06_地图与传送",
}


def is_real_bmp(path: Path) -> bool:
    try:
        return path.read_bytes()[:2] == b"BM"
    except Exception:
        return False


def classify(rel: str, name: str, parent: str) -> str:
    text = f"{rel}\\{name}".replace("/", "\\")
    stem = Path(name).stem

    # life-king coded tiles: BT1, QQ12, R100, C3, NG1, 1线A...
    if re.fullmatch(r"(BT|QQ|NG|C|R)\d+", stem, re.I) or re.fullmatch(r"\d+线A?", stem):
        return "19_生活王编码图块"
    if re.fullmatch(r"[A-Za-z]{1,4}\d{1,4}", stem) and "生活王" in rel:
        return "19_生活王编码图块"

    for cat, pats in RULES:
        for pat in pats:
            if re.search(pat, text, re.IGNORECASE):
                return cat
    if parent in PARENT_MAP:
        return PARENT_MAP[parent]
    # extra name hints for 无前缀
    name_rules = [
        ("09_血条与状态UI", [r"血", r"蓝条", r"怒", r"HP", r"MP"]),
        ("03_技能与技能栏", [r"技能", r"F\d", r"快捷"]),
        ("17_生活采集", [r"药", r"矿", r"鱼", r"锄", r"采", r"铁", r"铜", r"银", r"金"]),
        ("06_地图与传送", [r"山", r"城", r"谷", r"岭", r"湖", r"海", r"原", r"林", r"寺", r"洞", r"岛", r"河", r"镇", r"堡"]),
        ("02_确认取消按钮", [r"确定", r"取消", r"关闭", r"出去", r"离开", r"同意", r"拒绝"]),
        ("10_宠物宝宝", [r"宠", r"兽", r"捕"]),
        ("01_对话与选项", [r"点击", r"对话", r"选项", r"是$", r"否$"]),
        ("16_系统提示", [r"标志", r"提示", r"成功", r"失败", r"完成"]),
        ("07_任务栏与追踪", [r"任务", r"师门", r"追踪"]),
        ("08_NPC与角色", [r"人$", r"使$", r"商", r"老板", r"掌柜"]),
    ]
    for cat, pats in name_rules:
        for pat in pats:
            if re.search(pat, stem):
                return cat
    if "生活王" in rel and "_无前缀" in rel:
        return "20_生活王无前缀杂项"
    if "挖矿" in rel and "_无前缀" in rel:
        return "21_挖矿无前缀杂项"
    return "99_未分类"


def safe_name(s: str) -> str:
    return re.sub(r'[<>:"/\\|?*]', "_", s)


def suggest_use(cat: str) -> str:
    return {
        "01_对话与选项": "L2 对话选项模板；师门/送信",
        "02_确认取消按钮": "点击确认/取消",
        "03_技能与技能栏": "加工/生活技能图标",
        "04_进度条": "加工/捕兽有无进度条",
        "05_背包与物品": "背包认物、药品",
        "06_地图与传送": "地图块弱匹配/寻路辅助",
        "07_任务栏与追踪": "师门任务状态",
        "08_NPC与角色": "NPC 模板",
        "09_血条与状态UI": "血条形态参考；% 仍用框内图色",
        "10_宠物宝宝": "出战/休息",
        "11_怪物相关": "少量任务怪；非通用BOSS",
        "12_主线剧情": "主线弱匹配",
        "13_副本": "副本弱匹配",
        "14_仓库交易商店": "仓库/商店 UI",
        "15_好友组队社交": "社交 UI",
        "16_系统提示": "系统提示",
        "17_生活采集": "挖矿采药相关",
        "18_其他UI控件": "杂项控件",
        "19_生活王编码图块": "需对照生活王配置解读；勿盲目当通用UI",
        "20_生活王无前缀杂项": "人工点名后再升到具体类",
        "21_挖矿无前缀杂项": "人工点名后再升到具体类",
        "99_未分类": "人工复查后再挂接",
    }.get(cat, "")


def main() -> None:
    if OUT.exists():
        shutil.rmtree(OUT)
    OUT.mkdir(parents=True)

    rows: list[dict] = []
    cat_count: Counter[str] = Counter()
    pack_count: Counter[str] = Counter()
    size_bins: Counter[str] = Counter()
    used_names: dict[str, int] = defaultdict(int)

    files = [p for p in SRC.rglob("*.bmp") if is_real_bmp(p)]
    print(f"valid_bmp={len(files)}")

    for src in sorted(files, key=lambda p: str(p).lower()):
        rel = src.relative_to(SRC)
        pack = rel.parts[0] if rel.parts else ""
        parent = src.parent.name
        cat = classify(str(rel), src.name, parent)
        cat_count[cat] += 1
        pack_count[pack] += 1

        try:
            with Image.open(src) as im:
                w, h = im.size
                mode = im.mode
        except Exception:
            w = h = 0
            mode = "?"

        m = max(w, h)
        if m <= 16:
            size_bins["tiny_le16"] += 1
        elif m <= 48:
            size_bins["small_le48"] += 1
        elif m <= 128:
            size_bins["medium_le128"] += 1
        else:
            size_bins["large_gt128"] += 1

        stem = safe_name("__".join(rel.parts))
        if stem.lower().endswith(".bmp"):
            stem = stem[:-4]
        used_names[stem] += 1
        if used_names[stem] > 1:
            stem = f"{stem}__{used_names[stem]}"

        dest_dir = OUT / cat
        dest_dir.mkdir(parents=True, exist_ok=True)
        dest = dest_dir / f"{stem}.bmp"
        shutil.copy2(src, dest)

        rows.append(
            {
                "category": cat,
                "category_desc": CATEGORIES.get(cat, ""),
                "pack": pack,
                "source_rel": str(rel).replace("\\", "/"),
                "dest_rel": str(dest.relative_to(OUT)).replace("\\", "/"),
                "filename": src.name,
                "width": w,
                "height": h,
                "mode": mode,
                "bytes": src.stat().st_size,
                "usable_for": suggest_use(cat),
            }
        )

    csv_path = OUT / "catalog.csv"
    with csv_path.open("w", encoding="utf-8-sig", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        writer.writeheader()
        writer.writerows(rows)

    summary = {
        "total_usable_bmp": len(rows),
        "output_root": str(OUT),
        "source_root": str(SRC),
        "by_category": dict(cat_count.most_common()),
        "by_source_pack": dict(pack_count.most_common()),
        "by_size_bin": dict(size_bins),
        "categories": CATEGORIES,
        "notes": [
            "仅包含文件头为 BM 的标准 BMP；加密/DMDM/零前缀伪 BMP 已排除。",
            "主要来源：生活王1.4.0、挖矿2.15。",
            "分类按路径与文件名规则自动归类；99_未分类建议人工抽查。",
            "适合 L2 模板匹配；尺寸多为小图。",
        ],
    }
    (OUT / "catalog_summary.json").write_text(
        json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8"
    )

    md: list[str] = []
    md.append("# UU 可用图色模板分类目录\n\n")
    md.append(f"- **输出目录**：`{OUT}`\n")
    md.append(f"- **可用真 BMP 总数**：**{len(rows)}**\n")
    md.append("- **索引**：同目录 `catalog.csv`、`catalog_summary.json`\n")
    md.append("- **源目录**（未改动）：`data/imported/templates/`\n\n")
    md.append("## 按分类统计\n\n")
    md.append("| 分类 | 数量 | 说明 | 建议用途 |\n|---|---:|---|---|\n")
    for cat, n in cat_count.most_common():
        md.append(
            f"| `{cat}` | {n} | {CATEGORIES.get(cat, '')} | {suggest_use(cat)} |\n"
        )
    md.append("\n## 按来源包\n\n")
    md.append("| 源包 | 真 BMP 数 |\n|---|---:|\n")
    for pack, n in pack_count.most_common():
        md.append(f"| {pack} | {n} |\n")
    md.append("\n## 尺寸分布\n\n")
    for k, n in size_bins.most_common():
        md.append(f"- `{k}`: {n}\n")
    md.append("\n## 说明\n\n")
    for note in summary["notes"]:
        md.append(f"- {note}\n")
    md.append("\n## 与架构关系\n\n")
    md.append("| 层级 | 如何用这些图 |\n|---|---|\n")
    md.append("| L2 模板匹配 | 直接用本目录分类 BMP |\n")
    md.append("| L1 YOLO | 仅作小图标参考，主数据仍用客户区全屏采集 |\n")
    md.append("| 打怪 BOSS | `11_怪物相关` 极少；BOSS 靠目标面板模型+名字 |\n")

    readme = OUT / "README.md"
    readme.write_text("".join(md), encoding="utf-8")

    if VISION_DOCS.is_dir():
        shutil.copy2(readme, VISION_DOCS / "UU可用模板分类说明.md")
        shutil.copy2(csv_path, VISION_DOCS / "UU可用模板_catalog.csv")
        shutil.copy2(OUT / "catalog_summary.json", VISION_DOCS / "UU可用模板_catalog_summary.json")

    print("done", len(rows), "->", OUT)
    for cat, n in cat_count.most_common():
        print(f"  {n:4d}  {cat}")


if __name__ == "__main__":
    main()
