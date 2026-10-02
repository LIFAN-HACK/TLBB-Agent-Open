# 龙伴行目标模式主计划（自包含版）

## 1. 结论

本文是当前阶段的唯一执行入口。后续开发先看本文，再进入被引用的专项文档核查细节。

本文件已经合并引用文档中的关键结论，不只是列路径。引用文档保留用于追溯来源、查看完整细节和验证证据。

当前目标：先把“挂机打怪、打死换怪、玩家保护、回血回蓝、宠物技能、自动回点、死亡回点、安全清包”做成真实可运行闭环。

暂不把精力分散到抢小、抢反、马贼、跑商、帮贡、师门等二期功能。

## 2. 文档引用与已合并内容

### 2.1 产品需求

来源：

- `docs/requirements/蓝钻UU_ClaudeCode产品重构开发文档_v3.md`
- `docs/architecture/TLBB_Agent_ProductScope.md`
- `docs/architecture/TLBB_Agent_FinalProductBacklog.md`
- `docs/architecture/TLBB_Agent_FinalDeliveryChecklist.md`

已合并到本文的内容：

- 产品统一命名为“龙伴行”。
- 第一阶段只做 32 位稳定版。
- 新前端、新配置、新日志、新交互。
- 不引入旧登录、旧注册、旧充值、旧授权链。
- 功能迁移优先旧主程序中已经成熟的逻辑，不自由发明新玩法。
- 第一阶段核心是窗口绑定、按键打怪、挂机返点、死亡处理、清包、日志和配置。
- 所有未完成能力必须诚实标注，不伪装完成。

### 2.2 CODEX 注意事项与 Claude 分析

来源：

- `docs/CODEX_开发注意事项_Claude分析.md`

已合并到本文的内容：

- 当前代码方向正确，不推倒重来。
- 保留 UI → ViewModel → Contracts → Core 的分层。
- Core 不依赖 WPF。
- WinAPI 只能在 Core 服务层使用。
- `TemplateOcrService`、`InputService`、各 Controller 的服务化边界继续保留。
- 普通挂机不应该强依赖 OCR 才能跑。
- 旧主程序的“按键打怪”本质接近定时发键循环。
- OCR / 图色是增强能力，不应该阻塞基础打怪。
- 挂机返点不是独立主模式，而应该作为普通挂机的子能力。
- 公共 CD 后续如有并行能力，需要按窗口或通道隔离。

### 2.3 旧功能运作地图

来源：

- `docs/modules/BlueDiamond_FunctionLogicMap.md`

已合并到本文的内容：

- 识别层是“读游戏状态”的唯一入口，不允许业务控制器到处直接截图或解析。
- 战斗识别核心区域包括：
  - 目标名
  - 目标血条
  - 人物血条
  - 人物蓝条
  - 宠物血条
- 这些固定 UI 区域应抽成全局“战斗识别区域中心”，不要在每个模式重复校准。
- 模式差异只保留关键词、专属识别区和专属流程。
- 普通挂机状态机应包含：
  - Idle
  - BindWindow
  - CheckRoleStatus
  - SelectTarget
  - ValidateTarget
  - AttackLoop
  - CheckHpMpPet
  - ReturnPointIfNeeded
  - LootOrNextTarget
  - ErrorRecovery
  - Stop
- “挂机返点、死亡返点、土灵返点”必须挂在普通挂机内，不再作为和按键打怪互斥的主模式。

### 2.4 数据复用计划

来源：

- `docs/migration/BlueDiamond_ReusePlan.md`
- `docs/research/BlueDiamond_SourceInventory.md`
- `docs/research/BlueDiamond_ModuleComparison.md`

已合并到本文的内容：

- 可复用数据包括字库、OCR 模板、坐标、路线、快捷键、血蓝阈值、清包配置、题库、流程配置。
- 按键打怪可复用旧配置中的技能间隔、快捷键、回血、回蓝、治疗、宠物回血等字段思想。
- 坐标路线优先复用旧数据中的地图和坐标格式。
- 清包保留项、随身银行、随身商店、御龙帖等旧能力先作为配置来源和动作蓝本。
- 旧授权 DLL、旧账号字段、旧远端授权请求不进入新项目。

### 2.5 战斗后端决策

来源：

- `docs/architecture/ADR-002-CombatBackend.md`
- `docs/architecture/ADR-007-NoMemoryWriteAndRiskReducedGrab.md`
- `docs/architecture/LongBanXing_RuntimeState_TargetMode.md`
- `docs/architecture/LongBanXing_ReadOnlyMemoryResearchPlan.md`

已合并到本文的内容：

- 当前阶段禁用内存写入、注入、Hook、补丁和在线对象表扫描。
- 自动打怪主线保留外部截图识别 + 后台按键/点击链路。
- 战斗方式保留用户选择：
  - 自动判断
  - 游戏内置挂机
  - 程序按键打怪
- 自动判断策略：
  - 野外场景优先游戏内置挂机监控。
  - 副本或游戏提示无法开启内置挂机时，使用程序按键打怪。
- 程序按键打怪不能因为 OCR 失败而完全停摆。
- OCR 只做辅助增强，例如打死换怪、血量判断、玩家保护、只打红怪。
- 只读进程探针只用于进程元信息、版本、位数、SHA256、窗口尺寸，不默认接管业务。

### 2.6 挂机与回点组合决策

来源：

- `docs/architecture/ADR-003-HangReturnPointComposition.md`
- `docs/architecture/TargetMode_ReturnPoint_Cleanup.md`
- `docs/runtime/ReturnPointDiagnostics.md`

已合并到本文的内容：

- 回点是普通挂机的子能力，不是独立互斥模式。
- `NormalHangController` 每轮战斗前应调用 `IReturnPointRuntimeService`。
- 如果触发回点、死亡、卡点、土灵返回，本轮暂停攻击、技能、回血回蓝，由回点能力接管。
- 回点完成后恢复原挂机模式。
- 外部按键打怪和游戏内置挂机都必须接回点链路。
- 游戏内置挂机模式下，程序不发送选怪/普攻/技能，只监控坐标、死亡、卡点和回点。

### 2.7 战斗识别区域中心

来源：

- `docs/architecture/ADR-005-CombatRegionCenter.md`
- `docs/architecture/ADR-006-ResolutionProfile.md`
- `docs/runtime/CombatRecognitionDiagnostics.md`
- `docs/runtime/TemplateOcrDiagnostics.md`

已合并到本文的内容：

- 识别区域按客户区尺寸保存画像，不绑定单一分辨率。
- 每个分辨率可以有独立区域配置。
- 当前重点分辨率：
  - 1920x1080
  - 2560x1369
  - 1024x768 作为历史兼容画像
- 每次校准必须能导出截图证据。
- 识别区域不允许只靠默认硬编码长期运行。
- 目标玩家判定要依赖目标面板结构、头像、门派标识、玩家特征条等组合特征。

### 2.8 清包目标

来源：

- `docs/architecture/TargetMode_ReturnPoint_Cleanup.md`
- `docs/runtime/RuntimeVerification_20260621.md`
- `docs/migration/BlueDiamond_ReusePlan.md`

已合并到本文的内容：

- 清包当前默认 `DryRun`。
- 先扫描、识别、列候选，不默认出售。
- 未识别物品默认保护。
- 贵重物品默认保护。
- 任务物品默认保护。
- 绑定物品默认保护。
- 自动出售必须显式开启并经过保护规则。
- 背包自动校准只适合默认背包位置；拖动过背包建议手动网格校准。

### 2.9 验证与诊断

来源：

- `docs/runtime/BuildAndRun.md`
- `docs/runtime/WindowBindingDiagnostics.md`
- `docs/runtime/NormalHangDiagnostics.md`
- `docs/runtime/CombatRecognitionDiagnostics.md`
- `docs/runtime/InputServiceDiagnostics.md`
- `docs/runtime/ReadinessChecks.md`

已合并到本文的内容：

- 每个核心功能必须有诊断命令或可复现日志。
- 每次改动至少跑：
  - `dotnet build TlbbAgent.sln`
  - `TlbbAgent.Diagnostics readiness`
  - 涉及打怪跑 `normal-hang`
  - 涉及识别跑 `combat-recognition`
  - 涉及回点跑 `return-points`
  - 涉及清包跑 `bag-cleanup`
- 真实窗口验证必须输出截图或 JSON 证据。

## 3. 当前不变边界

1. 只处理龙伴行项目、自有旧程序、自有授权数据、自有配置和采样资产。
2. 不修改原始授权数据目录。
3. 不修改原始旧程序样本。
4. 不引入旧登录、注册、充值、改密、会员验证。
5. 不写游戏内存。
6. 不注入 DLL。
7. 不 Hook 游戏函数。
8. 不绕过第三方安全组件、验证码、风控、反作弊。
9. 不把研究代码默认接入业务主流程。
10. 不把未完成能力伪装成完成。

## 4. 目标功能模式

### 4.1 挂机打怪

目标：稳定可用，OCR 失败也能基础运行。

执行逻辑：

1. 启动时确认已绑定 32 位游戏窗口。
2. 读取当前战斗方式：
   - 自动判断
   - 游戏内置挂机
   - 程序按键打怪
3. 如果是程序按键打怪：
   - 没有目标或需要换怪时发送选怪键。
   - 识别到目标后执行目标合法性判断。
   - 新目标合法后普通攻击只触发一次。
   - 技能按各自秒级计时器循环释放。
   - Buff 技能按冷却释放。
   - 目标死亡、目标丢失、目标非法时切换目标。
4. 如果是游戏内置挂机：
   - 程序不发送选怪、普攻、技能。
   - 程序只负责开启/关闭内置挂机、监控坐标、死亡、卡点、回点。
5. 如果是自动判断：
   - 野外优先游戏内置挂机。
   - 副本或无法开启内置挂机时切程序按键打怪。

关键要求：

- 技能默认可按“始终”定时释放，不强制依赖“有目标”。
- OCR 只增强，不阻断。
- 日志必须说明当前实际战斗方式。
- 切换目标时必须先判断目标是否合法。

### 4.2 打死换怪

目标：目标死亡或丢失后能换下一个目标。

执行逻辑：

1. 读取目标血条百分比。
2. 如果目标血条为 0 或目标面板消失，判定目标死亡/丢失。
3. 发送选怪键。
4. 等待短暂稳定时间。
5. 重新进入目标合法性判断。

降级策略：

- 目标血量识别失败时，不进入“打死换怪精准模式”。
- 可退化为定时选怪/抢怪模式。

### 4.3 玩家目标保护

目标：默认不攻击玩家，避免被卡攻击模式。

判定依据按优先级组合：

1. 目标面板是否出现玩家头像。
2. 目标血条左侧是否存在门派标识区域。
3. 目标是否具备玩家面板结构。
4. 目标状态条是否出现蓝气条或怒气条。
5. 名字颜色仅作为辅助，不作为唯一依据。

注意：

- 不能只用“蓝条 + 怒气条同时存在”判断玩家，因为玩家蓝量或怒气可能为空。
- 不能只用红色血条判断，因为怪物、BOSS、小头目也有红色血条。
- 判断为玩家时立即停止攻击当前目标，发送选怪，日志记录“跳过玩家目标”。

### 4.4 血蓝与宠物技能

目标：回血、回蓝、血迹、共生、宠物回血不互相卡死。

人物状态：

- 吃药回血：人物血量低于阈值触发，药品 CD 默认 10 秒。
- 吃药回蓝：人物蓝量低于阈值触发，药品 CD 默认 10 秒。

宠物技能：

- 血迹回蓝：宠物技能，给角色回蓝，默认独立 CD 30 秒。
- 共生回血：宠物技能，给角色回血，默认独立 CD 30 秒。
- 血迹/共生优先支持点击宠物技能槽。
- 不要求玩家必须把宠物技能拖到人物技能栏。

宠物状态：

- 宠物出战：默认出战当前宠物。
- 宠物回血：宠物血量低于阈值时触发宠粮/宠物回血动作。

防卡死要求：

- 技能进入 CD 后不能每秒无限重按。
- 回血/回蓝失败不能阻断打怪主循环。
- 每个触发都要写日志，日志必须包含当前百分比、阈值、动作和冷却。

### 4.5 自动回点

目标：挂机过程中偏离点位、死亡、卡点时能恢复。

配置：

- 挂机点
- 土灵点
- 死亡点
- 地图名
- 坐标
- 偏离距离阈值
- 回点动作
- 恢复动作

执行逻辑：

1. 每轮打怪前检查是否需要回点。
2. 如果偏离距离超过阈值，暂停攻击。
3. 执行回点动作。
4. 监控回点结果。
5. 成功后恢复原挂机模式。
6. 连续失败进入卡点恢复。

游戏内置挂机模式：

- 需要回点时暂停或关闭内置挂机。
- 回点完成后重新开启内置挂机。

### 4.6 死亡回点

目标：死亡后进入独立恢复流程。

执行逻辑：

1. 识别死亡关键词或死亡状态。
2. 停止当前攻击/技能。
3. 执行复活、回城或死亡回点动作。
4. 回到配置点位。
5. 恢复挂机。

防卡死要求：

- 死亡识别失败不能无限按键。
- 每一步必须有超时和重试上限。
- 超过上限进入错误状态并等待用户处理。

### 4.7 安全清包

目标：先做到安全识别，不误卖。

执行逻辑：

1. 打开背包。
2. 识别背包网格。
3. 扫描格子占用情况。
4. 根据白名单、任务物品、绑定物品、未知物品策略生成候选。
5. 默认只显示候选，不出售。
6. 用户显式开启出售后，才允许执行出售动作。

保护规则：

- 未识别：保护。
- 贵重：保护。
- 任务物品：保护。
- 绑定物品：保护。
- 白名单：保护。
- 黑名单或低价值候选：只列候选，默认不卖。

## 5. 执行顺序

### 第一组：挂机稳定性

1. 修正普通挂机循环，拆分普通攻击、选怪、技能计时器。
2. 技能默认按秒计时，不再使用毫秒输入。
3. 技能默认支持 F1-F10，不扩展到更高功能键。
4. 修改日志，让日志显示真实快捷键，不显示旧缓存键位。
5. OCR 失败时不阻塞技能循环。

### 第二组：目标安全

1. 加入玩家目标保护。
2. 加入宠物/自身目标保护。
3. 非法目标后必须自动切换。
4. 输出目标面板截图证据。
5. 支持用户采集门派标识模板。

### 第三组：状态和宠物技能

1. 校准人物血条、蓝条、目标血条、宠物血条。
2. 加入吃药回血、吃药回蓝公共 CD 10 秒。
3. 加入血迹回蓝、共生回血独立 CD 30 秒。
4. 加入宠物技能槽点击测试和位置采集。
5. 加入宠物回血/宠粮逻辑。

### 第四组：回点和死亡

1. 接入地图与坐标识别。
2. 完成挂机点保存和偏离判断。
3. 完成土灵回点。
4. 完成死亡回点。
5. 完成卡点恢复。

### 第五组：清包

1. 完成背包网格识别。
2. 完成白名单/保护名单配置。
3. 完成候选列表。
4. 完成模拟清包诊断。
5. 出售动作延后，必须显式确认后再做。

## 6. 验收标准

必须全部可实际验证：

1. 绑定 32 位游戏窗口成功。
2. 启动后能稳定打怪。
3. 不攻击玩家目标。
4. 目标死亡后能换怪。
5. 技能释放节奏稳定。
6. OCR 失败时基础打怪不断。
7. 人物低血能回血。
8. 人物低蓝能回蓝。
9. 血迹/共生不会因为冷却卡死。
10. 宠物低血有处理逻辑。
11. 偏离挂机点能回点。
12. 死亡后能进入死亡回点流程。
13. 清包能识别背包并生成候选。
14. 默认不出售物品。
15. 配置能实时保存。
16. 关键动作都有日志。
17. 识别区域能导出截图证据。

## 7. 每次提交前验证

基础验证：

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build TlbbAgent.sln
& "C:\Program Files\dotnet\dotnet.exe" run --no-build --project tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj -- readiness
```

涉及挂机：

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run --no-build --project tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj -- normal-hang
```

涉及识别：

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run --no-build --project tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj -- combat-recognition
```

涉及回点：

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run --no-build --project tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj -- return-points
```

涉及清包：

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run --no-build --project tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj -- bag-cleanup
```

涉及真实窗口只读证据：

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run --no-build --project tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj -- readonly-probe
```
