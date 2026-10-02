# ADR-002：生产感知链统一为新视觉模型

日期：2026-07-10  
状态：已采用

## 决策

生产运行链统一为：

```text
LatestFrameService
  -> Core L1 V2 七类布局 ONNX
  -> L1-Aux 固定面板定位
  -> L2 Target HP / Player / Pet / Skill / Minimap / OpenText / Panel Slot
  -> PerceptionHub
  -> RuntimePerceptionSnapshot
  -> Controller / slot_action_planner
  -> 仅执行规划器确认的 click
```

旧点阵字库 OCR、旧固定战斗区域 OCR、`ScreenshotOcrRuntimeStateProvider` 不再作为生产回退路径。

主程序按 x64 运行。视觉链只处理客户区像素与窗口输入，因此可绑定 64 位或 32 位游戏客户端；不会按游戏位数切换模型，也不接入 32 位注入链。

## 生产 DI

- `ICombatRecognitionService` 暂由 `VisionCombatRecognitionAdapter` 兼容旧调用签名，数据来自 `IPerceptionHub`。
- `ITemplateOcrService` 暂由 `DisabledLegacyTemplateOcrService` 兼容前端构造函数，始终不加载字库、不输出识别结果。
- 外部战斗后端为 `VisionCombatBackend`。
- 运行快照只注册只读进程基础探针，不注册 `ScreenshotOcrRuntimeStateProvider`。

兼容接口只用于避免尚未同步迁移的前端/离线工具直接崩溃，不代表旧识别仍可在生产运行。

## 当前模型能力

| 能力 | 状态 | 生产行为 |
|---|---|---|
| 七类核心布局 | 已接入 | R6 `layout_v2_7class.onnx`，目标、人物、宠物、技能栏、小地图一次定位 |
| 目标血量插槽 | 已接入 | L2 ONNX；已确认存活后，高置信空血一帧立即判死；冷启动空面板保留两帧确认 |
| 目标开放文本 | 已接入 | L2 OpenText，按需运行 |
| 目标身份（玩家/怪物/BOSS/宠物） | 未训练 | 返回 `Unknown/Unavailable`；红名与近期主动伤害证据标记为不可用，玩家反击门禁拒绝放行 |
| 人物血蓝、宠物血量 | 已接入 | L1 裁剪后由 L2 颜色槽读取；宠物缺失连续两次才输出 `RestingOrAbsent` |
| 技能栏、CD | 已接入 | 只读取底部七个可见快捷键槽；技能图标身份与 CD 状态分离 |
| 血迹/共生 | 已接入 | 固定 UI 多模板识别；只有身份匹配且 CD 就绪时规划点击 |
| 小地图怪物时序三态 | 已接入 | 单帧只提供观察；最近 1200ms 有阳性即 `Present`，仅 5 个连续无怪帧跨 800ms 且 3 次找怪失败才确认 `Absent`；资源点不计怪物 |
| 小地图坐标 | 已接入 | 像素数字模板优先，Tesseract 仅作为回退；结构校验失败返回未知 |
| 地图名 | 已接入 | 使用 L1 小地图框内的 L2 OpenText ROI，按窗口缓存刷新 |
| 材料/月卡/商店/背包/死亡/寻路面板 | 已接入服务层 | L1-Aux 多尺度定位，L2 只消费面板裁剪 |

## 失败语义

1. L1 不可用：目标状态为 `Unknown`，不读取旧 OCR。
2. 有目标框但血量插槽不确定：状态为 `Ambiguous`，不判死亡。
3. 已确认存活的目标出现一次高置信空血：立即输出 `DeadByHpBottom`；未建立存活历史时，连续两个不同帧为空血才确认。
4. OpenText 不可用：名称为空；BOSS 白名单模式不得绕过白名单。
5. 人物/宠物模型不可用：百分比为空，不触发回血、回蓝或宠粮动作。
6. 小地图单帧 `Absent/Unknown`：只对小地图门禁输出 `noop`，不独立停止基础打怪；只有时序确认 `Absent` 才暂停无效选怪。
7. 面板、控件、物品策略任一未知：动作规划结果必须为 `noop`。

## 坐标契约

- `WindowCaptureService` 返回客户区图像。
- Core L1、L1-Aux 和所有 L2 的坐标均为客户区坐标。
- L2 只接收 L1 裁剪；返回的局部坐标由宿主加一次 L1 左上角偏移。
- 不叠加桌面窗口位置，不二次叠加边框偏移。

## 运行节奏

- L1 布局：最多每 125ms 刷新一次。
- 人物状态：150ms。
- 宠物状态：200ms。
- 技能栏/CD：150ms。
- 小地图怪物与坐标：200ms；缓存结果按 L2 采样序号去重，不得把同一结果重复累计为多帧。
- 地图名 OpenText：900ms。

L2 通过常驻 Python 宿主运行，避免每轮重复导入 NumPy、Pillow 和 OpenCV。

## 验证记录

- 视觉离线夹具：目标血量插槽 `9/9`，L1 目标面板 `5/5`，V2 核心槽及安全策略通过。
- R6 ONNX SHA256：`4321d65b3047d26f07db88821f07dd066f0c7914cef5415544d555cb849aa53b`。
- V2 样本输出：人物 HP/MP、宠物出战与 HP、七个技能槽、小地图三态和坐标均来自真实离线样本。
- 安全动作：未知小地图、冷却技能、非法材料动作均为 `noop`；未列入策略的物品为 `KeepItem`。
- 小地图回归：UU 圈闪烁保持阳性；单帧无点不停止；5 帧/800ms 无点并伴随 3 次找怪失败才确认无怪；资源点不计怪物；同一缓存帧重放不累计。
- 时序：冷启动首帧空血为 `Ambiguous`，同帧重复不累计，第二个不同帧确认；已确认存活后下一帧高置信空血立即确认为 `DeadByHpBottom`。
- 战斗回归：打死换怪、抢怪、弱血续打、1-5% 活目标、BOSS 禁止预测切换、黑名单均通过。
- 玩家反击证据链：身份证据经目标判定、运行快照和战斗映射完整传播；任一证据不可用时失败闭锁。
- 真实窗口只读验证：L1 与目标血量插槽持续输出 `Live`，未发送任何输入。

## 后续删除条件

当前端与离线采样工具不再构造 `ITemplateOcrService` / `ICombatRecognitionService` 后，删除：

- `TemplateOcrService`
- `CombatRecognitionService`
- `ScreenshotOcrRuntimeStateProvider`
- `OcrCombatBackend`
- `PerceptionSourceKind.LegacyOcr`
- `CombatBackendPreference.ExternalOcr` 兼容别名
