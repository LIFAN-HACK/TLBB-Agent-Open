# ADR-003：角色会话、幂等命令与结构化运行快照

- 状态：已采纳，基础运行时已实现
- 日期：2026-07-10
- 依据：`docs/requirements/LongBanXing_Frontend_Backend_Handoff_20260710.md`
- 范围：Contracts / Core；不包含正式前端布局

## 1. 决策

龙伴行后端不再以“一份全局当前窗口 + 一套全局运行状态”作为新功能接口。

新的高层能力必须绑定：

- `RoleSessionId`
- `CharacterProfileId`
- `WindowHandle`
- `ConfigRevision`
- `CommandId`

前端只发送声明式配置和高层命令，并读取结构化快照；不得从日志文本推断运行状态。

## 2. 已实现契约

实现位置：

- `src/TlbbAgent.Contracts/RoleSessionContracts.cs`
- `src/TlbbAgent.Core/RoleSessionManager.cs`

当前保证：

1. 同时最多四个有效角色会话。
2. 同一 HWND 只能属于一个会话。
3. 同一 `RoleSessionId` 不允许静默切换 `CharacterProfileId`。
4. 更换 HWND 时取消旧运行令牌、清空旧功能快照和命令缓存，但保留配置修订。
5. 同一 `CommandId` 只执行一次，重复提交返回原结果。
6. `ExpectedRevision` 不是当前修订时拒绝写入，防止自动保存覆盖新配置。
7. 每个会话拥有独立取消令牌和功能快照。
8. 关闭一个会话不会影响其他会话。
9. 已关闭的 `RoleSessionId` 不允许在同一运行期复用。

公共协议：

- `FeatureCommand<T>`：幂等命令信封。
- `SessionCommandResult<T>`：成功、冲突、拒绝或故障结果。
- `RoleSessionSnapshot`：角色会话只读状态。
- `FeatureRuntimeSnapshot`：模块只读运行状态。

## 3. 喊话队列迁移

实现位置：

- `src/TlbbAgent.Contracts/ShoutRuntimeContracts.cs`
- `src/TlbbAgent.Core/ShoutQueueRuntimeService.cs`
- `src/TlbbAgent.Core/Automation/ShoutController.cs`

已实现：

1. 配置、队列游标、暂停状态和冷却按角色会话隔离。
2. 条目按稳定 ID 和顺序执行；重排后不会立即重复刚提交的条目。
3. 暂停保留游标，暂停期间不继续发送输入。
4. 世界频道固定冷却 180 秒。
5. 世界频道冷却和队列游标按 `CharacterProfileId` 持久化，重启不能绕过。
6. 提交结果无法验证时保守计入本轮，避免重复发送。
7. 每个条目提供等待、冷却、选频道、发送、成功和失败状态。
8. 旧 `ShoutController` 已改为兼容门面，实际执行统一进入新运行时。

## 4. 生产能力边界

`IShoutChannelSelector` 目前生产实现为 `UnavailableShoutChannelSelector`。

原因：尚未取得七种聊天频道的可靠选择样本或已验证动作。后端会返回“频道未就绪”，不会假启动，也不会把坐标或测试参数暴露给正式前端。

因此当前结论是：

- 会话、队列、冷却、顺序、暂停和持久化：真实实现，回放已通过。
- 七频道现场选择与实际发送闭环：未就绪，需取得可验证的游戏样本后实现 `IShoutChannelSelector`。

## 5. 回放验收

角色会话：

```powershell
dotnet run --project tools/TlbbAgent.SessionHarness/TlbbAgent.SessionHarness.csproj -c Debug
```

覆盖：四会话上限、HWND 独占、角色档案隔离、窗口替换、命令幂等、修订冲突、快照和取消令牌。

喊话队列：

```powershell
dotnet run --project tools/TlbbAgent.ShoutHarness/TlbbAgent.ShoutHarness.csproj -c Debug
```

覆盖：双角色队列隔离、顺序、暂停、运行时重排、修订、命令幂等、世界冷却和跨运行时恢复。

## 6. 后续接入顺序

1. 正式前端改为显式维护 `RoleSessionId`，不再依赖全局当前窗口。
2. 角色配置写入统一携带 `ExpectedRevision`。
3. 启动、暂停、停止、记录点位和截图添加统一携带 `CommandId`。
4. 挂机、返点、杀怪、BOSS 和副本逐个迁移到会话级运行时。
5. 取得频道选择样本后实现真实 `IShoutChannelSelector`，再开放喊话启动入口。

旧单例控制器在迁移期间只作为兼容门面，不再作为新增状态和冷却的存储位置。
