#!/usr/bin/env python3
"""Build local skill metadata and raw icon seeds from configured official pages."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import time
from dataclasses import dataclass, field
from html.parser import HTMLParser
from pathlib import Path
from tempfile import NamedTemporaryFile
from typing import Any
from urllib.parse import urljoin, urlparse
from urllib.request import Request, urlopen


USER_AGENT = "TLBBAgentSkillLibrary/1.0 (+local research dataset)"
SECT_CONFIG = {
    "少林": {"slug": "shaolin", "prefix": "sl"},
    "明教": {"slug": "mingjiao", "prefix": "mj"},
    "丐帮": {"slug": "gaibang", "prefix": "gb"},
    "武当": {"slug": "wudang", "prefix": "wd"},
    "峨嵋": {"slug": "emei", "prefix": "em"},
    "星宿": {"slug": "xingxiu", "prefix": "xs"},
    "天龙": {"slug": "tianlong", "prefix": "tl"},
    "天山": {"slug": "tianshan", "prefix": "ts"},
    "逍遥": {"slug": "xiaoyao", "prefix": "xy"},
    "慕容": {"slug": "murong", "prefix": "mr"},
}
XIAOYAO_HANG_USABLE = {
    "落英剑",
    "弹指神功",
    "溪山行旅",
    "排山倒海",
    "鲲跃北溟",
}


@dataclass
class Cell:
    text: list[str] = field(default_factory=list)
    images: list[str] = field(default_factory=list)


class SkillTableParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.rows: list[list[Cell]] = []
        self._row: list[Cell] | None = None
        self._cell: Cell | None = None

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        tag = tag.lower()
        if tag == "tr":
            self._row = []
        elif tag in {"td", "th"} and self._row is not None:
            self._cell = Cell()
            self._row.append(self._cell)
        elif tag == "img" and self._cell is not None:
            src = dict(attrs).get("src")
            if src:
                self._cell.images.append(src)
        elif tag == "br" and self._cell is not None:
            self._cell.text.append("\n")

    def handle_endtag(self, tag: str) -> None:
        tag = tag.lower()
        if tag in {"td", "th"}:
            self._cell = None
        elif tag == "tr" and self._row is not None:
            self.rows.append(self._row)
            self._row = None
            self._cell = None

    def handle_data(self, data: str) -> None:
        if self._cell is not None:
            self._cell.text.append(data)


def normalize_text(parts: list[str]) -> str:
    value = "".join(parts).replace("\xa0", " ")
    value = re.sub(r"[ \t\r\f\v]+", " ", value)
    value = re.sub(r" *\n *", "\n", value)
    return value.strip()


def parse_skills(html: str, page_url: str) -> list[dict[str, str]]:
    parser = SkillTableParser()
    parser.feed(html)
    skills: list[dict[str, str]] = []
    for row in parser.rows:
        if len(row) < 2 or not row[0].images:
            continue
        name_lines = [item.strip() for item in normalize_text(row[0].text).splitlines() if item.strip()]
        if not name_lines:
            continue
        name = name_lines[0]
        body = normalize_text(row[1].text)
        if "简介" not in body and "详细描述" not in body:
            continue
        intro_match = re.search(r"简介[：:]\s*(.*?)(?=\n?详细描述[：:]|$)", body, re.S)
        detail_match = re.search(r"详细描述[：:]\s*(.*)$", body, re.S)
        intro = normalize_text([intro_match.group(1)]) if intro_match else ""
        detail = normalize_text([detail_match.group(1)]) if detail_match else ""
        skills.append(
            {
                "name": name,
                "intro": intro,
                "detail": detail,
                "icon_url": urljoin(page_url, row[0].images[0]),
            }
        )
    return skills


def request_bytes(url: str, timeout: float) -> tuple[bytes, str]:
    request = Request(url, headers={"User-Agent": USER_AGENT, "Accept": "*/*"})
    with urlopen(request, timeout=timeout) as response:
        return response.read(), response.headers.get_content_type()


def atomic_write(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with NamedTemporaryFile(dir=path.parent, delete=False) as temp:
        temp.write(data)
        temp_path = Path(temp.name)
    temp_path.replace(path)


def image_extension(url: str, content_type: str) -> str:
    suffix = Path(urlparse(url).path).suffix.lower()
    if suffix in {".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"}:
        return ".jpg" if suffix == ".jpeg" else suffix
    return {"image/png": ".png", "image/jpeg": ".jpg", "image/bmp": ".bmp"}.get(content_type, ".img")


def infer_kind(name: str, text: str) -> str:
    combined = f"{name} {text}"
    if any(token in combined for token in ("恢复血", "治疗", "回复血")):
        return "heal"
    if any(token in combined for token in ("多个目标", "范围", "群体", "周围", "陷阱", "阵法")):
        return "aoe"
    if any(token in combined for token in ("攻击", "伤害")):
        return "damage"
    if any(token in combined for token in ("提升", "增加", "状态", "免疫", "抗性", "防御")):
        return "buff"
    return "other"


def infer_policy_metadata(name: str, intro: str, detail: str) -> dict[str, Any]:
    text = f"{name} {intro} {detail}"
    tag_rules = {
        "passive": ("被动蕴含", "修习之后可以提升"),
        "control": ("定身", "封穴", "失明", "散功", "围困", "冷冻", "冰封", "僵硬"),
        "stealth": ("隐身",),
        "movement": ("瞬间移动", "移动速度", "互换位置"),
        "stance": ("姿态", "防御状态", "攻击状态"),
        "cleanse_or_immunity": ("解除", "免疫"),
        "ground_target": ("点选地面", "点击地面"),
        "friendly_target": ("友方目标", "附近队友", "个队友"),
        "multi_target": ("群体", "多个敌人", "多个目标", "周围"),
        "health_cost": ("损失一定的血", "需要血量"),
        "conditional_cast": ("只有在", "需要装备", "需要折梅式", "需要血量"),
        "pvp_sensitive": ("任何玩家", "目标与自己的位置互换"),
    }
    tags = sorted(tag for tag, tokens in tag_rules.items() if any(token in text for token in tokens))
    if "passive" in tags:
        decision = "disabled_passive"
    else:
        decision = "manual_review_required"
    if "ground_target" in tags:
        targeting = "ground"
    elif "friendly_target" in tags:
        targeting = "friendly_or_self"
    elif any(token in text for token in ("单体攻击", "单体近身攻击", "单体远程攻击", "对目标")):
        targeting = "enemy"
    elif any(token in text for token in ("自己", "自身", "状态")):
        targeting = "self"
    else:
        targeting = "unknown"
    return {
        "release_decision": decision,
        "auto_release_allowed": False,
        "review_state": "official_seed_only",
        "targeting": targeting,
        "safety_tags": tags,
        "requires_ingame_calibration": True,
        "requires_manual_policy_review": True,
    }


def load_sources(path: Path, sects: set[str]) -> list[dict[str, Any]]:
    payload = json.loads(path.read_text(encoding="utf-8"))
    sources = []
    for source in payload.get("primary_sources", []):
        if source.get("status") != "official_usable":
            continue
        if sects and source.get("sect") not in sects:
            continue
        sources.append(source)
    return sources


def load_catalog(path: Path) -> list[dict[str, Any]]:
    if not path.exists():
        return []
    output = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.strip():
            output.append(json.loads(line))
    return output


def apply_policy_overrides(records: list[dict[str, Any]], path: Path) -> set[str]:
    if not path.exists():
        return set()
    payload = json.loads(path.read_text(encoding="utf-8"))
    overrides = payload.get("skills", {})
    applied: set[str] = set()
    for record in records:
        override = overrides.get(record["skill_id"])
        if not isinstance(override, dict):
            continue
        applied.add(record["skill_id"])
        if "kind" in override:
            record["kind"] = override["kind"]
        if "hang_usable" in override:
            record["hang_usable"] = bool(override["hang_usable"])
        policy = override.get("automation_policy")
        if isinstance(policy, dict):
            record["automation_policy"] = {**record.get("automation_policy", {}), **policy}
    return applied


def write_report(path: Path, source_results: list[dict[str, Any]], failures: list[dict[str, str]]) -> None:
    total = sum(item["skills"] for item in source_results)
    missing = sum(item["missing_icons"] for item in source_results)
    lines = [
        "# 官方技能抓取报告",
        "",
        f"- 官方门派页：{len(source_results)}",
        f"- 技能记录：{total}",
        f"- 缺失图标：{missing}",
        f"- 失败请求：{len(failures)}",
        "- 运行时资格：raw 图标仅作元数据/校准种子，运行时禁止直接匹配",
        "",
        "## 明细",
        "",
        "| 门派 | 技能数 | 缺图 | 来源 |",
        "|---|---:|---:|---|",
    ]
    for item in source_results:
        lines.append(f"| {item['sect']} | {item['skills']} | {item['missing_icons']} | {item['url']} |")
    lines.extend(["", "## 失败 URL", ""])
    if failures:
        lines.extend(f"- `{item['url']}`：{item['error']}" for item in failures)
    else:
        lines.append("无。")
    atomic_write(path, ("\n".join(lines) + "\n").encode("utf-8"))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--sources", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    parser.add_argument("--sect", action="append", default=[])
    parser.add_argument("--delay-ms", type=int, default=250)
    parser.add_argument("--timeout", type=float, default=20.0)
    parser.add_argument("--refresh", action="store_true")
    parser.add_argument("--policy-overrides", type=Path)
    args = parser.parse_args()

    root = args.output_root.resolve()
    source_dir = root / "sources"
    raw_dir = root / "icons_raw"
    report_dir = root / "reports"
    for directory in (source_dir, raw_dir, report_dir, root / "icons_ingame", root / "match_index"):
        directory.mkdir(parents=True, exist_ok=True)

    selected_sects = set(args.sect)
    sources = load_sources(args.sources, selected_sects)
    if not sources:
        raise SystemExit("No official_usable sources matched the requested sect.")

    failures: list[dict[str, str]] = []
    source_results: list[dict[str, Any]] = []
    new_records: list[dict[str, Any]] = []
    for source in sources:
        sect = str(source["sect"])
        sect_config = SECT_CONFIG.get(sect)
        slug = sect_config["slug"] if sect_config else hashlib.sha1(sect.encode("utf-8")).hexdigest()[:8]
        prefix = sect_config["prefix"] if sect_config else slug
        page_url = str(source["url"])
        snapshot = source_dir / f"{slug}_{Path(urlparse(page_url).path).name or 'index.html'}"
        try:
            if args.refresh or not snapshot.exists():
                page_bytes, _ = request_bytes(page_url, args.timeout)
                atomic_write(snapshot, page_bytes)
                time.sleep(max(0, args.delay_ms) / 1000.0)
            else:
                page_bytes = snapshot.read_bytes()
            skills = parse_skills(page_bytes.decode("utf-8", "replace"), page_url)
            if not skills:
                raise RuntimeError("page parsed but no skill rows were found")
        except Exception as exc:
            failures.append({"url": page_url, "error": str(exc)})
            continue

        missing_icons = 0
        for index, skill in enumerate(skills, start=1):
            skill_id = f"{prefix}_{index:03d}"
            icon_url = skill["icon_url"]
            existing = next(raw_dir.glob(f"{skill_id}.*"), None)
            icon_path: Path | None = existing
            if icon_path is None or args.refresh:
                try:
                    icon_bytes, content_type = request_bytes(icon_url, args.timeout)
                    extension = image_extension(icon_url, content_type)
                    icon_path = raw_dir / f"{skill_id}{extension}"
                    atomic_write(icon_path, icon_bytes)
                    time.sleep(max(0, args.delay_ms) / 1000.0)
                except Exception as exc:
                    failures.append({"url": icon_url, "error": str(exc)})
                    icon_path = None
            if icon_path is None:
                missing_icons += 1

            desc = " ".join(part for part in (skill["intro"], skill["detail"]) if part)
            new_records.append(
                {
                    "skill_id": skill_id,
                    "name": skill["name"],
                    "sect": sect,
                    "kind": infer_kind(skill["name"], desc),
                    "hang_usable": sect == "逍遥" and skill["name"] in XIAOYAO_HANG_USABLE,
                    "desc": desc,
                    "intro": skill["intro"],
                    "detail": skill["detail"],
                    "icon_raw": icon_path.relative_to(root).as_posix() if icon_path else "",
                    "icon_ingame": "",
                    "source_url": page_url,
                    "source_icon_url": icon_url,
                    "source_kind": "official_web_seed",
                    "runtime_match_eligible": False,
                    "calibration_scope": "global_library_per_client_version",
                    "source_order": index,
                    "automation_policy": infer_policy_metadata(skill["name"], skill["intro"], skill["detail"]),
                    "version_tags": ["official-web", "tlbbgl"],
                }
            )
        source_results.append({"sect": sect, "skills": len(skills), "missing_icons": missing_icons, "url": page_url})

    override_path = args.policy_overrides or root / "policy_overrides.json"
    policy_override_ids = apply_policy_overrides(new_records, override_path)

    catalog_path = root / "catalog.jsonl"
    existing = load_catalog(catalog_path)
    replaced_sects = {record["sect"] for record in new_records}
    existing_by_skill = {record.get("skill_id"): record for record in existing}
    for record in new_records:
        previous = existing_by_skill.get(record["skill_id"])
        if previous is None or previous.get("name") != record.get("name"):
            continue
        variants = list(previous.get("icon_ingame_variants", []))
        icon_ingame = previous.get("icon_ingame", "")
        if variants or icon_ingame:
            record["icon_ingame"] = icon_ingame
            record["icon_ingame_variants"] = variants
            record["runtime_match_eligible"] = bool(previous.get("runtime_match_eligible"))
            record["hang_usable"] = bool(previous.get("hang_usable"))
        previous_policy = previous.get("automation_policy")
        if (
            record["skill_id"] not in policy_override_ids
            and isinstance(previous_policy, dict)
            and previous_policy.get("review_state") != "official_seed_only"
        ):
            record["automation_policy"] = previous_policy
    merged = [record for record in existing if record.get("sect") not in replaced_sects] + new_records
    merged.sort(key=lambda record: record["skill_id"])
    catalog_bytes = "".join(json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n" for record in merged)
    atomic_write(catalog_path, catalog_bytes.encode("utf-8"))
    atomic_write(report_dir / "scrape_failures.json", json.dumps(failures, ensure_ascii=False, indent=2).encode("utf-8"))
    write_report(report_dir / "scrape_report.md", source_results, failures)
    print(json.dumps({"sources": source_results, "failures": len(failures), "catalog": str(catalog_path)}, ensure_ascii=False))
    return 1 if not new_records else 0


if __name__ == "__main__":
    raise SystemExit(main())
