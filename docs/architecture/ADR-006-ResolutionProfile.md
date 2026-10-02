# ADR-006: ResolutionProfile 按客户区尺寸保存 OCR 与头像校准

## 状态

Accepted

## 背景

历史版本 实测并不强制 1024×768。用户的游戏窗口尺寸可能在 1024×768、1280×720、1531×955、最大化等状态之间变化。

仅依赖一次全局坐标缩放会有风险：

- 某个尺寸下手动校准过的区域无法被稳定复用。
- 窗口尺寸改变后，投影区域不一定等价于该尺寸下的最佳 OCR 区域。
- 左侧绑定区头像截图也会受客户区尺寸变化影响。

## 决策

新增 `IResolutionProfileService` / `ResolutionProfileService`，按真实客户区尺寸保存校准。

当前覆盖：

- 战斗 OCR 区域：
  - 目标名
  - 目标血
  - 人物血
  - 人物蓝
  - 宠物血
- 左侧绑定区头像截图区域

运行规则：

- `CombatRecognitionService` 每次先抓完整客户区，再用真实截图宽高查找 ResolutionProfile。
- 命中当前尺寸 profile 时优先使用该 profile。
- 未命中时回退 `GlobalConfig.CombatRecognition`。
- `WindowCaptureService.CaptureAvatar` 也按真实客户区宽高查找头像区域。
- 未命中头像 profile 时回退默认头像区域 `6,6,224,86`。
- 头像来源 `AvatarSource` 与战斗 OCR 来源 `Source` 分开记录，避免互相覆盖。

## 存储

ResolutionProfile 暂时通过 `IProfileService` 保存为内部方案：

```text
resolution-profiles.json
```

该内部方案已从用户左侧“配置方案”列表中过滤，不污染用户可选方案。

## 不做什么

- 不强制把游戏窗口改成 1024×768。
- 不把 32 位稳定版绑定到单一分辨率。
- 不用 ResolutionProfile 替代模式独立配置；路线、坐标、关键词、活动提示区域仍属于各模式。

## 验证

命令：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --no-build --project '.\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj' -- resolution-profile
```

验证点：

- 1531×955 命中战斗 OCR profile。
- 1024×768 未命中时回退默认配置。
- 头像区域按 1531×955 命中保存的区域。
- 头像未命中尺寸回退默认 `6,6,224,86`。
- 战斗来源与头像来源分开保存。

当前结果：

```text
ExactProfile=True
MissingProfile=False
LoadedSource=diagnostic
LoadedAvatarSource=diagnostic-avatar
AvatarRegion=31,41,151,61
FallbackAvatar=6,6,224,86
PlayerHp=80
PlayerMp=70
PetHp=55
PASS
```

## 已完成

- 数据调试页“战斗 OCR 校准”卡片已增加“保存当前尺寸”按钮。
- 刷新客户区预览后显示当前尺寸是否已有 OCR 校准。
- 保存时抓取当前真实客户区尺寸，并把当前通用战斗 OCR 区域写入 `ResolutionProfileService`。
- 头像截图运行链路已纳入 ResolutionProfile。

## 后续

- 增加头像区域的 UI 校准入口。
- 增加 ResolutionProfile 列表、删除、重命名、导入导出。
- 实机验证常见尺寸：1024×768、1280×720、1531×955、窗口最大化。
