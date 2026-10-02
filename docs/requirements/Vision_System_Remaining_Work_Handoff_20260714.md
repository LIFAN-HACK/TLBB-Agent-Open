# 视觉系统剩余工作交接说明（2026-07-14）

## 结论

本轮已完成右侧技能栏、BUFF 锚点、五类交互窗口的模型打包与真实推理接入，并将材料、背包和便捷动作迁移到新的安全边界。核心目标框/HP、动态技能、当前已校准宠物恢复技能和小地图时序链路可继续运行；交互窗口目前只允许检测和生成只读计划，购买、出售、银行转移、月卡点击和生产动作均保持关闭。

不得把“模型存在”解释为“业务动作已开放”。`runtime_authorized=false` 是当前正确发布状态。

## 运行链路

```text
CaptureResult(frame_sequence)
  -> L1: layout / right_skill_bar / buff_anchor / interaction_window
  -> L2: HP / OpenText / skill identity+CD / interaction controls
  -> PerceptionHub or InteractionReadOnlyPlanService
  -> business policy
  -> feature flag + asset authorization + fresh-frame checks
  -> action executor (only for an explicitly authorized module)
```

所有交互动作计划必须保留：`window_id`、`frame_sequence`、`panel_class`、`panel_bbox_px`、`control_id/slot_id`、`safe_click_bbox_px`、点击点或按键、身份置信度、策略结论、后置条件、超时和重试上限。旧帧不得连续点击。

## 已打包模型

| 模块 | 运行目录 | SHA256 | 状态 |
|---|---|---|---|
| 核心 L1 | `models/vision/l1_layout_v2/layout_v2_7class.onnx` | `903e52f163d2c3467bdb1ec2ea2f8b83df07937e813c0c67d62d4f7305d90a43` | 发布 |
| 目标 HP L2 | `models/vision/target_hp_slot/target_hp_slot_detect.onnx` | `cd006238f44523bc0780478eab01883f867f453b9c3459f1b4df48af47285ef0` | 发布 |
| 右栏 L1 640 | `models/vision/right_skill_bar_v2/right_skill_bar_v2_640.onnx` | `fb59e520924e6967311a2423b3223718a1b2b16e54596c8106956f548da623d3` | 发布 |
| 右栏 L1 1280 | `models/vision/right_skill_bar_v2/right_skill_bar_v2_1280.onnx` | `efcbd2bb235f281fa7341f8452de2929e27cb77573ec35f4346c12c9aa022dc3` | 发布 |
| BUFF 锚点 L1 | `models/vision/buff_anchor_r3/buff_anchor_detect.onnx` | `07f2f73ef15abd74225980e10719ed83f5216ccd1eaa7bb3b2308a550c332610` | feature flag 候选 |
| 交互窗口 L1 V3 | `models/vision/interaction_windows_v3/interaction_window_l1_v3_5class.onnx` | `9a7273fde40161ced55a82a3b56ba753f3d4dab0a642fafed40a270616fbc7a0` | 检测发布、动作只读 |

五类交互窗口是 `bag_panel`、`bank_panel`、`material_panel`、`monthly_card_panel`、`shop_panel`。精确控件资产位于 `models/vision/interaction_controls_v3/`，清单授权当前为 false。

## 服务和消费者

| 服务 | 输出 | 当前消费者 | 权限 |
|---|---|---|---|
| `OnnxLayoutDetectService` | 七类全局布局框 | `PerceptionHub`、挂机链路 | 可运行 |
| `TargetHpSlotService` | Alive / DeadByHpBottom / Unknown | `TargetJudgementService` | 可运行；Gone 与死亡分离 |
| `RightSkillBarDetectService` | 右侧两列技能槽 | `PerceptionHub`、`NormalHangController` | 可运行 |
| `VisionV2SlotService` | 技能身份、CD、宠物恢复槽 | `NormalHangController` | 仅 Matched + hang_usable + 当前帧可释放 |
| `BuffAnchorDetectService` | 最多 5 行 BUFF 几何 | `PerceptionHub` | `TLBB_AGENT_BUFF_ANCHOR_ENABLED` 默认关闭 |
| `BuffStateFusionService` | mounted/fatigue 三态时序 | 宠物技能和坐骑门禁 | 身份 unknown 时禁止动作 |
| `InteractionWindowDetectService` | 五类窗口框 | `PerceptionHub`、只读计划 | 可检测 |
| `InteractionControlCatalogService` | visual/safe/click、显式网格 | 材料、背包、只读计划 | `runtime_authorized=false` |
| `InteractionReadOnlyPlanService` | 帧绑定的控件/槽位计划 | `UtilityController`、后续专用状态机 | 永不直接发输入 |
| `BagCleanupService` | 10x6 格子、物品策略候选 | 清包页、`UtilityController` | 实际出售硬阻断 |

## 已落地行为

### 动态技能

- 底栏和右栏按当前帧重建槽位，不依赖玩家摆放位置。
- 只有配置了 `ExpectedVisualSkillId`、命中游戏内模板、`hang_usable=true`、CD ready 且帧新鲜的技能可进入释放候选。
- 控制、位移、隐身、友方/PVP、被动等仍由策略库禁止；官网图标只作候选种子。
- 天山当前游戏内模板为 0，保持 unknown，不自动释放。

### BUFF 与宠物恢复

- BUFF 区按最多 5 行规划，不再只截三行。
- `mounted=unknown` 不切坐骑；`fatigue=present/unknown` 不点击血祭、共生等宠物主动技能。
- 血祭/共生支持宠物第一、第二技能槽，但只使用已校准模板或安全点。

### 交互窗口

- L1 V3 正向 104/104、纯负 50 张零假阳性。
- L2 V3 共 25 个 crop、230 个控件、50 个区域，跨窗口位置映射误差为 0。
- 背包使用显式 6x10 格；银行使用背包/仓库两组 8x10 显式格，锁定格保留为 `blocked_disabled_cell`。
- 旧 `(0,14)` 背包偏移不会重复叠加；坐标只从 V3 crop 映射一次。
- 旧 `UtilityController` 的随身银行/商店绝对坐标点击已阻断，只记录月卡精确控件的只读计划。

### 材料和清包

- 材料面板、配方、制造控件已迁移到 V3；真实动作要求稳定双帧、同一帧号控件映射和新帧后置验证。
- 生产环境控件清单未授权，因此材料动作保持 blocked。
- 背包分析优先要求 V3 面板和完整 60 格；缺面板、缺控件资产、帧过期或格子不完整时返回零候选。
- `BagCleanupService.Execute` 即使 `AllowActualSell=true` 也不会出售。

## 数据资产

- 视觉工作区：`<TLBB_VISION_ROOT>`。
- 原始审计：3187 张、78 个采集会话；连续帧按会话分组，重复和隔离项有独立清单。
- 技能库：252 条 catalog、159 张游戏内模板、19 个已校准 SkillId、92 ready、67 cooling；cooling 不进入身份模板。
- 官网十门派种子：247 张已恢复到复核包，但仍是 candidate，不授予释放权限。
- 物品库：`train/runtime_db/item_assets_v1/`，20 个条目、22 张原像素完整边框模板；复制前后哈希一致。当前 20 个均 `never_sell=true`，可售项为 0。

## 明确未发布

- 目标类型 L2：已有 485 个视觉候选，但玩家/NPC 未拆分，灰名怪没有独立确认覆盖。
- NPC/自动寻路/死亡/确认框：分别存在 7/1/5/1 帧等数据缺口；统一返回 `capture_required`，不点击任何 OCR 行。
- 月卡和银行：检测模型通过当前离线集，但独立真实采集会话不足，动作不授权。
- BUFF 身份：锚点模型已过回归，mounted/fatigue 模板族未过人工门槛。
- 物品出售：没有达到门槛的 `sell_candidate` 资产。

具体最少采集量见视觉工作区 `train/reports/remaining_goal_20260714/capture_queue.jsonl`。

## 补采后的续跑命令

以下命令均在 `<TLBB_VISION_ROOT>` 执行。先把新截图放回原始模块目录并完成人工复核，再运行对应流程；脚本输出仍是 candidate 时，不得手工改成发布状态。

```powershell
# 所有补采批次先重建不可变清单、会话分组、缺口和最终状态报告
python train/scripts/134_build_remaining_goal_raw_audit.py
python train/scripts/136_build_remaining_goal_asset_status.py
python train/scripts/138_build_remaining_goal_release_reports.py

# 目标类型：重建候选复核包和训练数据；未完成人工金标时停在数据集阶段
python train/scripts/07_prepare_target_type_icon_labels.py
python train/scripts/05_prepare_target_kind_dataset.py

# NPC、自动寻路、死亡和确认框：先重建导航/对话复核包
python train/scripts/133_build_navigation_dialog_l1_review.py

# 月卡、银行及其余五类窗口：重建时间线、训练集、L2 精确控件和负样本门禁
python train/scripts/121_prepare_interaction_window_l1_review.py
python train/scripts/125_build_interaction_window_timeline_review.py
python train/scripts/126_build_interaction_window_l1_dataset.py
python train/scripts/131_build_interaction_window_l2_reannotation_queue.py
python train/scripts/132_build_interaction_window_l2_v3_precise.py
python train/scripts/130_build_interaction_window_negative_gate.py

# BUFF 身份：重扫多分辨率完整区域并生成复核工作区；不得只裁三行
python train/scripts/79_prepare_buff_review_workspace.py
python train/scripts/82_mine_buff_bar_multires.py
python train/scripts/98_review_1659_buff_frames.py

# 物品和技能：重建物品库；仅导入已经人工核准的游戏内技能模板
python train/scripts/137_build_item_assets_v1.py
python train/scripts/87_import_verified_skill_library.py
```

需要重新训练五类交互窗口时，人工标注通过后依次运行 `127_remote_train_interaction_window_l1_v1.sh`、`128_remote_eval_export_interaction_window_l1_v1.sh`、`129_evaluate_interaction_window_candidate.py`；只有发布门槛和独立负样本门禁同时通过，才允许重新打包进运行工程。任何模型或资产变化后都必须重新运行 VisionHarness、CombatHarness、VisionDeploymentHarness 和本页末尾的 Release 验证。

## 后端后续约束

1. 不再读取 `UtilityActionStep.ClickX/ClickY` 执行月卡、银行或商店动作。
2. 只从 `InteractionReadOnlyPlanService` 的精确 `control_id` 选择候选；禁止模糊名称映射。
3. 即使设置 `TLBB_AGENT_INTERACTION_ACTIONS_ENABLED=true`，控件清单仍必须单独人工改为授权；两者缺一都不得执行。
4. 实际动作必须使用专用状态机，执行前复核窗口、帧号和安全区，执行后读取新帧验证后置条件。
5. NPC 选项必须由未来发布的共享 `npc_dialog_panel` + OpenText 行框 + 版本化精确白名单完成，不复用“抢 NPC”世界目标服务。
6. 物品 matcher 在达到出售门槛前只能输出 `keep/unknown_protected`，不得把图标相似度单独当出售依据。

## 验证与回滚

完整验证结果见 `docs/runtime/Vision_System_Remaining_Work_Verification_20260714.md`。逐模块机器可读状态位于视觉工作区 `train/reports/remaining_goal_20260714/status_matrix.json`。

回滚时只切换 `artifacts/product/current-release.txt` 到上一版本目录；不要原地覆盖 ONNX。保持两个动作 feature flag 未设置，即可维持只读安全状态。
