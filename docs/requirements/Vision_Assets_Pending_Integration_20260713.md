# 视觉资产待接入清单 2026-07-13

## 1. 已确认不是待接入项

以下两项已经真实接入，后端不要重复实现：

- L1 V4 七类布局：运行文件虽名为 `models/vision/l1_layout_v2/layout_v2_7class.onnx`，但 SHA256 为 V4 的 `903e52f163d2c3467bdb1ec2ea2f8b83df07937e813c0c67d62d4f7305d90a43`。
- 目标血量 L2：`target_hp_slot_detect.onnx`，SHA256 `cd006238f44523bc0780478eab01883f867f453b9c3459f1b4df48af47285ef0`。

2026-07-13 实测 VisionHarness：L1 `5/5`、HP L2 `9/9`、整体 `PASS`。

## 2. 模型已就绪、等待后端接入

### 2.1 右侧技能栏 L1

按以下专项任务书执行：

`docs/requirements/RightSkillBar_L1_DualProfile_Backend_Handoff_20260713.md`

必须使用 640/1280 双档 ONNX、源像素几何门控和固定 `2 x 10` 槽位重建。未完成接入前保持 unknown/noop。

### 2.2 BUFF 锚点 L1

视觉资产：

`<TLBB_VISION_ROOT>\train\artifacts\l1_buff_anchor_r3_precise_reviewed_candidate\`

- ONNX：`buff_anchor_detect.onnx`
- SHA256：`07f2f73ef15abd74225980e10719ed83f5216ccd1eaa7bb3b2308a550c332610`
- 状态：`candidate_passed_current_suite_feature_flag_only`

接入要求：独立服务、模型哈希钉扎、feature flag、无合法框返回 unknown；不得把 BUFF 候选直接当作 mounted/fatigue 身份。

## 3. 暂时不要接入为自动点击

九类 `l1_ui_aux` 当前是标题模板定位，不是经过独立原生测试的窗口检测模型。外部窗口复测已发现：

- 材料窗口左移后被旧搜索 ROI 排除。
- 785x617 背包被 ROI 边界漏掉。
- 相似金色标题栏造成月卡/材料对背包的假阳性。

当前 32 张按钮模板只能在可靠窗口 crop 内作为 L2 证据。`interaction_ui_reannotation_v2` 的 111 个旧粗控件框不得导入点击数据库。

新窗口复核包：

`<TLBB_VISION_ROOT>\train\review\interaction_window_l1_v3_precise\`

状态：85 张原图、125 个提议、93 个高置信候选、32 个隔离项，尚未授权训练或运行。

新的五类 L2 V3 精标目录：

`<TLBB_VISION_ROOT>\train\review\interaction_window_l2_controls_v3_precise\`

- 25 个 L1 V3 crop；46 个控件定义；10 个结构/识别区域。
- 每个控件同时给出视觉框、安全点击框和点击点；背包/银行格子使用显式几何。
- 自动边界与点击包含校验 `0` 错误。
- 当前状态：`codex_pixel_audited_pending_user_review_not_runtime_authorized`。

后端现在可以读取该目录做 DTO/解析器适配，但人工叠加图复核完成前不得打开鼠标动作 feature flag。

## 4. 物品与清包边界

- 技能运行库有效：159 张游戏内模板、19 个 SkillId。
- 通用物品运行索引无有效模板：`data/samples/bag-items/index.json` 的 `templateCount=0`。
- 旧 UU 背包图标未完成当前客户端校准，不能生成出售权限。
- `BagCleanupService.Execute` 当前明确阻断实际出售，应继续保留。
- 未知物品统一 `unknown_protected/noop`。

## 5. 后端完成顺序

1. 接右侧技能栏双档 L1，并进入 VisionHarness/CombatHarness。
2. 接 BUFF 锚点 L1，但身份 L2 未就绪前只输出 unknown。
3. 接入已完成的交互窗口 L1 V3 五类模型；L2 精标只先接解析和只读调试，不开放动作。
4. 等当前客户端物品图标库、名称绑定和盲测完成后，才讨论自动出售。

完整训练与采集矩阵见：

`<TLBB_VISION_ROOT>\docs\CODEX_视觉资产待接入与待训练总表_20260713.md`

## 6. 新增待接入：交互窗口 L1 V3 五类模型

本文第 3 节关于“交互窗口 L1 尚未完成”的状态已被本节覆盖。视觉侧已经完成 V3 训练和独立门槛，但运行仓库尚未加载该模型。

- 模型目录：`<TLBB_VISION_ROOT>\train\artifacts\interaction_window_l1_v3_5class_candidate\`
- ONNX：`interaction_window_l1_v3_5class.onnx`
- SHA256：`9a7273fde40161ced55a82a3b56ba753f3d4dab0a642fafed40a270616fbc7a0`
- 类别：背包、商店/月卡/银行/材料加工五类整窗。
- 推理阈值：`0.75`；NMS IoU：`0.70`；`imgsz=960`。
- 正向原生门槛：54 张、104 实例，`FP=0 / FN=0`。
- 纯负样本门槛：50 张，`FP=0`。
- 状态：`model_ready_pending_backend_integration`。

专项交接：

`docs/requirements/Interaction_Window_L1_V3_Backend_Handoff_20260713.md`

注意：该模型只能替换五类窗口的主定位。旧 `l1_ui_aux` 模板可保留为 L2/降级证据，不能与 V3 结果并列直接触发点击。按钮和格子 L2 已完成 Codex 原像素精标，但仍待用户叠加图复核；清包出售、商店购买、月卡按钮、银行操作继续保持 `unknown/noop`。

## 7. 新增候选：对话、寻路与任务窗口

复核目录：

`<TLBB_VISION_ROOT>\train\review\navigation_dialog_l1_review_v1\`

当前真实覆盖：

| 类别 | 实例 | 后端状态 |
|---|---:|---|
| `quest_log_panel` | 49 | 仅候选数据；等跨分辨率模型与盲测 |
| `npc_dialog_panel` | 7 | 仅候选数据；普通对话 5、目的地 2 |
| `death_dialog` | 5 | 单会话连续帧；旧模板仍只作辅助证据 |
| `confirm_dialog` | 1 | 禁止模型发布或自动确认；OCR 白名单门控 |
| `auto_nav_panel` | 1 | 禁止模型发布或自动点击 |

接口统一：L1 使用 `npc_dialog_panel` 定位普通 NPC/目的地共用框体；`npc_destination_panel` 作为 L2 语义子类和兼容别名。后端不要同时运行两个近似外框分类器争抢同一窗口。目的地行、确认文本和自动寻路条目均由 L2 OCR/状态机处理，低置信统一 `unknown/noop`。
