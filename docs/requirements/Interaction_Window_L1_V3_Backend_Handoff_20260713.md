# 交互窗口 L1 V3 后端接入任务书

## 1. 结论

视觉侧已交付五类交互窗口整框检测 ONNX。该模型尚未进入 TLBB Agent 运行链，后端接入完成并通过 Harness 前，不得写成“已接入”。

模型只回答“窗口是什么、整框在哪里”，不回答“按钮是什么、应该点哪里”。

## 2. 资产

- 源目录：`<TLBB_VISION_ROOT>\train\artifacts\interaction_window_l1_v3_5class_candidate\`
- ONNX：`interaction_window_l1_v3_5class.onnx`
- ONNX SHA256：`9a7273fde40161ced55a82a3b56ba753f3d4dab0a642fafed40a270616fbc7a0`
- PT SHA256：`cca724058cc43394c8c4e95c8d9b8b87129cef2889a1a306f45fb2c5e76f6997`
- 模型卡：`model_card.md`
- 推理契约：`inference_contract.json`
- 发布门槛：`release_gate.json`

## 3. 类别映射

| id | class | 业务语义 |
|---:|---|---|
| 0 | `bag_panel` | 背包 |
| 1 | `shop_panel` | NPC 商店/购买窗口 |
| 2 | `monthly_card_panel` | 月卡/御龙帖 |
| 3 | `bank_panel` | 银行 |
| 4 | `material_panel` | 材料加工 |

不要把死亡、确认、NPC 对话、目的地、自动寻路、人物、宠物面板映射到这五类。

## 4. 推理要求

1. 输入必须是完整游戏客户区 RGB，不含 Windows 标题栏、任务栏和桌面边缘。
2. 保持宽高比 letterbox 到 `imgsz=960`，推理后逆变换到原始客户区像素。
3. 置信阈值使用 `0.75`，NMS IoU 使用 `0.70`，最大候选数 10。
4. 输出至少包含 `class_id / class_name / confidence / bbox_xyxy / frame_sequence`。
5. 框越界、宽高异常、帧序号过期或未稳定两帧时返回 `unknown/noop`。
6. 同类多框时不得直接选最大框；按置信度、完整边框和时序一致性排序，歧义时返回 unknown。
7. 模型文件启动时做 SHA256 钉扎，哈希不符禁止加载。

## 5. L1/L2 边界

- L2 输入必须来自已确认的 L1 crop。
- 禁止只用 L1 框比例生成按钮点击点。
- 商店测试中的单边最大误差约 `28.4 px`，控件必须使用独立 `visual_bbox`、`safe_click_bbox` 和 `click_point`。
- L2 原始队列位于：`<TLBB_VISION_ROOT>\train\review\interaction_window_l2_controls_v3_queue\`。
- L2 精确结果位于：`<TLBB_VISION_ROOT>\train\review\interaction_window_l2_controls_v3_precise\`。
- 精确结果包含 25 个 crop、46 个控件定义、10 个结构/识别区，自动边界/包含/点击点校验 `0` 错误。
- 背包 V3 crop 相对旧参考新增标题栏像素，旧参考控件坐标已统一修正 `(x+0, y+14)`；后端不得再次叠加偏移。
- 当前仍是 `pending_user_review_not_runtime_authorized`；背包出售、商店购买、月卡、银行点击全部保持禁用。

### L2 DTO 最低字段

```text
class_id
crop_size
visual_bbox_xyxy
safe_click_bbox_xyxy
click_point_xy
region.kind / rows / columns / cell_size_px / x_starts_px / y_starts_px
annotation_status
runtime_authorized
```

运行时只能从当前稳定 L1 bbox 映射 crop 内坐标；不得读取标注文件中的历史屏幕绝对坐标。`runtime_authorized=false` 时只允许画调试叠加图，不允许注入鼠标动作。

## 6. 旧实现处理

- `l1_ui_aux` 标题模板不再作为这五类窗口的主 L1。
- 旧模板可作为模型结果后的身份复核或模型 unknown 时的只读候选，不得独立触发动作。
- 现有材料加工控制链可以先接 V3 的 `material_panel` crop，但仍要保留按钮 L2、OCR 和后置状态检查。
- `BagCleanupService.Execute` 的实际出售阻断继续保留，直到物品库和右键出售流程独立验收。

## 7. 验收门槛

1. 模型哈希检查通过。
2. ONNX 解码与视觉侧 `inference_contract.json` 一致。
3. 将跨分辨率正向样本加入 VisionHarness，五类窗口均无漏检、无错类。
4. 将人物、宠物、任务、世界地图、聊天、NPC、BOSS 纯负样本加入 VisionHarness，不能产出五类窗口。
5. 材料加工现有 Harness 必须继续通过。
6. CombatHarness 证明单帧窗口消失、低置信框和歧义框均为 noop。
7. 未完成 L2 的类别不能产生鼠标动作。

完成上述七项后，才可把状态从 `model_ready_pending_backend_integration` 改为 `integrated_verified`。
