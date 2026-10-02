# -*- coding: utf-8 -*-
"""
Recover UU 'DMDM' pseudo-BMP assets when a plaintext BM twin exists.

Binary findings (static):
  - Format: magic 'DMDM' (4) + const LE 0xEF46631E (4) + per-file IV (8) + ciphertext
  - ciphertext length == plaintext BMP length
  - per-file keystream (no global stream reuse); not simple XOR/RC4/TEA with header keys
  - wm*.dll contains 'DMDM' magic checks + FindPic APIs; decrypt path likely VMProtect'd
  - Zero-prefix packs (单师门/挖图) are a different scheme; not recovered here

Strategy:
  - For each DMDM file, if another pack has same filename with real BM header, copy BM out.
"""
from __future__ import annotations

import json
import os
import shutil
from collections import defaultdict
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
SRC = REPO_ROOT / "data" / "imported" / "templates"
OUT = REPO_ROOT / "data" / "imported" / "templates-recovered"
REPORT = OUT / "recover_report.json"


def main() -> None:
    if OUT.exists():
        shutil.rmtree(OUT)
    OUT.mkdir(parents=True)

    by_name: dict[str, list[Path]] = defaultdict(list)
    for p in SRC.rglob("*.bmp"):
        by_name[p.name].append(p)

    recovered = []
    orphans = []
    dmdm_total = 0

    for name, paths in sorted(by_name.items()):
        bm_paths = []
        dm_paths = []
        for p in paths:
            head = p.read_bytes()[:4]
            if head[:2] == b"BM":
                bm_paths.append(p)
            elif head == b"DMDM":
                dm_paths.append(p)
                dmdm_total += 1
        if not dm_paths:
            continue
        if bm_paths:
            # prefer 挖矿 / 生活王 plaintext
            bm_paths_sorted = sorted(
                bm_paths,
                key=lambda p: (
                    0 if "挖矿" in str(p) else 1 if "生活王" in str(p) else 2,
                    len(p.parts),
                ),
            )
            src_bm = bm_paths_sorted[0]
            dest = OUT / "from_plaintext_twin" / name
            dest.parent.mkdir(parents=True, exist_ok=True)
            # avoid overwrite collisions
            if dest.exists():
                dest = OUT / "from_plaintext_twin" / f"{src_bm.parent.parent.name}__{name}"
            shutil.copy2(src_bm, dest)
            recovered.append(
                {
                    "name": name,
                    "dmdm_sources": [str(p.relative_to(SRC)).replace("\\", "/") for p in dm_paths],
                    "plaintext_from": str(src_bm.relative_to(SRC)).replace("\\", "/"),
                    "out": str(dest.relative_to(OUT)).replace("\\", "/"),
                    "method": "same_filename_plaintext_twin",
                }
            )
        else:
            for p in dm_paths:
                orphans.append(str(p.relative_to(SRC)).replace("\\", "/"))

    # also copy zero-prefix inventory (not decrypted)
    zero = []
    for p in SRC.rglob("*.bmp"):
        d = p.read_bytes()
        if d[:4] == b"\x00\x00\x00\x00":
            zero.append(str(p.relative_to(SRC)).replace("\\", "/"))

    report = {
        "dmdm_file_instances": dmdm_total,
        "recovered_via_plaintext_twin": len(recovered),
        "orphan_dmdm_no_twin": len(orphans),
        "zero_prefix_encrypted_count": len(zero),
        "format_notes": {
            "DMDM": "16-byte header + ciphertext; body len == BM len; per-file key; algorithm not recovered statically",
            "ZERO_PREFIX": "long zero prefix + high-entropy body; separate scheme (单师门/挖图); not recovered",
            "recommendation": "Use templates-usable (plaintext packs) + recovered twins; re-capture UI for orphans",
        },
        "recovered": recovered[:50],
        "recovered_total": len(recovered),
        "orphan_samples": orphans[:40],
        "zero_prefix_samples": zero[:20],
    }
    REPORT.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

    md = []
    md.append("# UU 伪 BMP 二进制解码尝试报告\n\n")
    md.append("## 结论摘要\n\n")
    md.append("| 格式 | 结果 |\n|---|---|\n")
    md.append("| **DMDM** | 格式已摸清；算法未从静态二进制完全还原；**可用同名明文 BMP 替换恢复** |\n")
    md.append(f"| DMDM 实例数 | {dmdm_total} |\n")
    md.append(f"| 经同名明文恢复 | **{len(recovered)}** → `templates-recovered/from_plaintext_twin/` |\n")
    md.append(f"| 无明文双胞胎的孤儿 | {len(orphans)} |\n")
    md.append(f"| 零前缀加密（单师门/挖图） | {len(zero)} 张，**本轮未解** |\n\n")
    md.append("## 格式\n\n")
    md.append("```text\n")
    md.append("offset 0..3   : 'DMDM'\n")
    md.append("offset 4..7   : 常量 1E 63 46 EF (文件中常见)\n")
    md.append("offset 8..15  : 每文件不同 (疑 IV/密钥材料)\n")
    md.append("offset 16..end: 密文，长度 = 明文 BMP 长度\n")
    md.append("```\n\n")
    md.append("密文 ⊕ 明文 = 高熵密钥流，**非** 单字节 XOR / 简单周期 XOR / 以 header 为 key 的 RC4。\n")
    md.append("跨文件密钥流不复用。`wm*.dll` 中有 `DMDM` 魔数判断与 `FindPic` 字符串；解密实现疑在 VMProtect 保护区。\n\n")
    md.append("## 建议\n\n")
    md.append("1. 日常模板用 `templates-usable`（生活王+挖矿明文）。\n")
    md.append("2. 马/队员马里缺的图：看 `templates-recovered` 是否已有同名恢复。\n")
    md.append("3. 孤儿 + 零前缀：优先 **游戏内重截** 或动态调试 DLL 解密函数（API hook 读出解压后缓冲）。\n")
    (OUT / "README.md").write_text("".join(md), encoding="utf-8")

    # copy report to vision docs
    vision = Path(
        os.environ.get(
            "TLBB_VISION_DOCS",
            str(Path.home() / "Desktop" / "TLBB 视觉模型" / "docs"),
        )
    )
    if vision.is_dir():
        shutil.copy2(OUT / "README.md", vision / "UU_BMP加密解码尝试.md")
        shutil.copy2(REPORT, vision / "UU_BMP_recover_report.json")

    print(json.dumps({k: report[k] for k in report if k not in ("recovered", "orphan_samples", "zero_prefix_samples")}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
