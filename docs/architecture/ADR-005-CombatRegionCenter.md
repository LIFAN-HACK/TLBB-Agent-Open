# ADR-005: 战斗 OCR 区域中心

## 状态

Accepted

## 背景

目标名、目标血量、人物血量、人物蓝量、宠物血量这 5 个区域属于天龙客户端固定战斗 UI 区域。它们不会因为抢 NPC、反贼、和尚、三环等模式变化而变成不同位置。

旧实现中，多个模式配置里各保存一份 `CombatRecognitionConfig`，默认值相同，容易造成以下问题：

- 用户需要在多个页面重复标定。
- 某个模式区域标错后，看起来像模式逻辑问题。
- 方案保存时产生冗余配置。

## 决策

运行时统一使用 `GlobalConfig.CombatRecognition` 作为战斗 OCR 区域中心。

当前接入：
- 普通挂机：通过 `OcrCombatBackend` 使用全局区域。
- 马贼：战斗识别使用全局区域。
- 抢 NPC：目标名识别使用全局区域。
- 自动反贼：战斗目标识别使用全局区域。
- 自动和尚：战斗目标识别使用全局区域。
- 三环副本：战斗阶段目标识别使用全局区域。

各模式原有 `CombatRecognitionConfig` 字段暂时保留，作为导入配置兼容字段，不再作为上述固定战斗 UI 区域的运行依据。

## 保留模式差异

以下内容仍然属于模式独立配置：

- 目标关键词，例如抢小、抢反、和尚、马贼、怪名。
- 导航地图/坐标识别区域。
- 死亡提示区域。
- 副本完成提示区域。
- 活动结束提示区域。
- 路线、坐标、点击点、热键、重试策略。

## 验证

新增诊断：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --no-build --project '.\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj' -- combat-region-center
```

验证方法：
- 把全局战斗区域设置为一个测试对象。
- 把抢 NPC 自己的旧区域故意设置为另一个错误对象。
- 执行抢 NPC 目标选择。
- 确认 `NpcTargetingService` 传给 OCR 的是全局区域对象，而不是模式旧区域对象。

当前结果：

```text
Found=True
UsedGlobalConfig=True
UsedLocalConfig=False
PASS
```

## 后续

UI 上应逐步弱化各模式页面里的固定战斗区域编辑入口，把标定入口集中到“通用配置 / 数据调试”的战斗 OCR 校准区域。
