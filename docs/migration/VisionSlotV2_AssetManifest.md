# 视觉插槽 V2 资产迁移清单

日期：2026-07-11

## 来源

只读来源根目录：

`<TLBB_VISION_ROOT>\train\artifacts`

## 迁移内容

| 来源 | 项目内目标 | 用途 |
|---|---|---|
| `l1_layout_v2_20260710_r6_pet_lowhp` | `src/TlbbAgent.Core/models/vision/l1_layout_v2` | 七类核心布局 ONNX、配置、类别与清单 |
| `l1_ui_aux` | `src/TlbbAgent.Core/models/vision/v2/l1_ui_aux` | 九类固定面板多尺度定位 |
| `l2_slots` | `src/TlbbAgent.Core/models/vision/v2/l2_slots` | 人物、宠物、技能、小地图、坐标和动作规划 |
| `fixed_ui_slots` | `src/TlbbAgent.Core/models/vision/v2/fixed_ui_slots` | 血迹、共生身份与 CD |
| `l2_ui_panels` | `src/TlbbAgent.Core/models/vision/v2/l2_ui_panels` | 面板内控件与安全动作候选 |

未复制训练权重 `.pt`、训练曲线、测试可视化、`__pycache__` 和 `.pyc`。原始目录未修改。

## 完整性

R6 ONNX：

```text
文件：layout_v2_7class.onnx
SHA256：4321d65b3047d26f07db88821f07dd066f0c7914cef5415544d555cb849aa53b
```

该哈希同时由来源配置、复制后的文件和离线 Harness 校验。

## 本地扩展

项目副本中的 `slot_action_planner.py` 增加了 `player_health` 规划，用于共生对人物回血；阈值语义统一为“当前值小于等于阈值时触发”。原始训练资产未被修改。

## 2026-07-11 小地图语义增量

需求原文已只读复制到：

`docs/requirements/给后端开发_小地图标记语义与状态机修正_20260711.md`

从只读来源 `train/artifacts/l2_slots` 同步：

| 资产 | SHA256 | 处理 |
|---|---|---|
| `l2_slot_worker.py` | `936f825f15b9b0d646fc1e2a842396d61dafee704b57b1cfe728d7aa9e475356` | 原样复制；输出怪物、UU 圈、原生/UU 橙点和资源点四类字段 |
| `minimap_temporal_fusion.py` | `37ac3ec83a9f55c2dd8b1225c919b95d11065444b10f37b174873a8a05cead69` | 原样复制，作为视觉侧语义基准与离线验证脚本依赖 |
| `templates/minimap_monster/manifest.json` | `f3b8738fccf15567a070fc0996855a0de55ea9ddfef52ec76c00c3a76f4f9a03` | 原样复制 |
| `orange_ring_1.png` | `7430d89072e16c91a79c69e385f39b23a32d3aa406316bf8b60534b504c1c0f5` | 原样复制 |
| `orange_ring_2.png` | `38474fbaa90a6d3137a87d9bc79d7581106916b8e962e49b189d7e3662001c58` | 原样复制 |
| `orange_ring_3.png` | `2d2274c2d3abd6979637a7113b0567fd8e05fbe910cc55c5f689b84e04091627` | 原样复制 |

`slot_action_planner.py` 没有整文件覆盖：项目原有 `player_health` 与恢复阈值语义必须保留，仅合并上游小地图“单帧无点为 noop、时序确认无怪才停止”的规则。项目清单已明确标注该覆盖项。
