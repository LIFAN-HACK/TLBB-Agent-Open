# ADR-004: 配置方案聚合保存

## 状态

Accepted

## 背景

左侧“配置方案”原本只保存通用配置。这样用户以为保存了一个完整方案，但马贼、抢 NPC、反贼、和尚、三环、返点、生活采集等模块配置没有进入同一个方案档案。

这会直接破坏产品使用预期：同一个角色或窗口的方案应当包含“通用配置 + 各模式独立配置”，而不是只保存首页字段。

## 决策

新增 `IAppProfileService` / `AppProfileService` 作为聚合配置服务：

- UI 只调用 `IAppProfileService.SaveAll(profileName)` 和 `LoadAll(profileName)`。
- 聚合服务负责保存/加载：
  - 通用配置
  - 返点配置
  - 马贼配置
  - 抢 NPC 配置
  - 自动反贼配置
  - 自动和尚配置
  - 三环配置
  - 生活采集配置
  - 跑商配置
  - 材料加工配置
  - 单师门配置
  - 挖图配置
  - 帮贡配置
  - 便捷配置
  - 自动重连配置
  - 自动上号配置
  - 喊话配置
- 各模块仍然保留自己的 ConfigService，聚合层只负责编排，不吞并模块边界。
- 内部模块配置使用固定前缀保存，例如 `horse-bandit-{profileName}`。
- 对用户展示的方案列表过滤内部前缀，只显示真实方案名。
- 聚合服务记录窗口到方案的映射；绑定同一窗口时，UI 可自动加载已记住方案。

## 不做什么

- 不把 UI 控件状态直接序列化成方案。
- 不让 MainWindow 或 ViewModel 自己逐项写文件。
- 不把所有模块配置合成一个巨大类后破坏现有服务边界。

## 验证

诊断命令：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --no-build --project '.\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj' -- app-profile
& 'C:\Program Files\dotnet\dotnet.exe' run --no-build --project '.\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj' -- profile-autosave
& 'C:\Program Files\dotnet\dotnet.exe' run --no-build --project '.\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj' -- profile-window-autoload
```

验证点：

- 保存 `诊断方案-聚合` 后，底层同时生成通用配置与各模块内部配置。
- 对外方案列表只显示 `诊断方案-聚合`，不暴露内部模块前缀。
- 修改内存配置后重新加载，通用配置、返点、马贼、抢 NPC、反贼、和尚、三环、生活采集、跑商、材料加工、单师门、挖图、帮贡、便捷、喊话等均可恢复。
- 前端 `MainViewModel` / `GlobalConfigPageViewModel` 修改攻击键、选怪键、回血回蓝键、技能键位、技能间隔和阈值后，会通过 800ms 防抖自动覆盖当前方案。
- 已记住窗口绑定到方案 A 后，即使当前最后方案是方案 B，再次绑定同一窗口也会自动加载方案 A。

当前结果：PASS。

当前窗口映射键是进程路径 + 窗口标题。它能覆盖同一角色窗口的重启/重绑，但不能保证区分多个同路径、同标题窗口；按角色自动加载需要后续接入稳定角色名识别或只读运行态角色名字段。

## 后续

当 P1 模块接入后，继续把跑商、材料加工、挖矿、挖图、单师门、帮贡、答题题库配置加入 `AppProfileService` 聚合清单。
