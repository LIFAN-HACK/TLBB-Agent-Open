# Quest Log Panel L1 V3 Candidate

## 作用

该单类检测器只定位完整客户区中的 `quest_log_panel` 外框。它不负责读取任务文字、判断任务状态，也不把 NPC 对话框当作任务日志。

## 本轮修复

- 将洛阳校场 NPC“龙霸天”对话框加入原生 hard-negative。
- 新增 24 张轻量全帧变体和 144 张结构 hard-negative，仅进入训练集。
- V2 的验证集、测试集保持不变。
- 训练和评估均按运行时固定 `960 x 960` letterbox 口径执行。

## 推理契约

- 输入：完整游戏客户区 RGB。
- 预处理：保持宽高比 letterbox 到 `[1,3,960,960]`，float32，除以 255。
- 后处理：`confidence=0.45`、`NMS IoU=0.70`、`maxDetections=5`。
- 输出：撤销 letterbox 后的源客户区 `xyxy` 像素坐标。
- 连续两帧确认后才能交给 L2；低置信或冲突状态返回 `unknown`。

## 结果与限制

ONNX 在未改动的 34 张测试图上：14/14 正样本命中，20 张负样本零误检，中位 IoU `0.990028`。龙霸天原始帧零检测。当前仍缺少独立采集的多分辨率任务面板正样本，因此 `runtimeAuthorized=false`，必须经后端实机回归后再授权。
