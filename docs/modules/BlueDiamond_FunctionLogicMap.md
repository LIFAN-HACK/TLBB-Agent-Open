# 蓝钻功能运作地图 (Function Logic Map)

> 目的：把每个功能的「逻辑 / 代码 / 配置 / 状态 / 相互关系」讲清楚，供前端交互架构决策。
> 基于当前代码实测梳理（2026-06）。只读分析，不改代码。

---

## 0. 一张图看懂数据主轴

所有功能都走同一条链，**没有例外**：

```
窗口绑定(WindowBindingService) ── 得到 hwnd
        │
        ▼
截图(WindowCaptureService.CaptureRegion) ── 按 OCR 区域裁子图
        │
        ▼
字库识别(TemplateOcrService.Recognize) ── 点阵模板匹配出文字
        │
        ▼
识别层(XxxRecognitionService) ── 把文字解析成"游戏状态"(血量%/目标名/坐标/死亡…)
        │
        ▼
控制器(XxxController : 独立状态机) ── 读配置 + 看状态 → 决定动作
        │
        ▼
输入(InputService.SendKey/Click) ── PostMessage/SendInput 发到 hwnd
        │
        ▼
日志(RuntimeLogService) ── 全程留痕
```

**给前端的含义**：每个功能页本质是在配置「① 识别哪些区域 ② 满足什么条件 ③ 发什么键/点哪里」。前端交互应围绕这三段组织，而不是按旧 UI 的零散输入框。

---

## 1. 共享脊柱（先理解这层，再看每个功能就很快）

### 1.1 控制器统一骨架
所有模式控制器都继承 `AutomationControllerBase<TState>`（`Core/Automation/`）：
- 持有一个**独立的** `StateMachine<TState>`（互不共用，符合架构边界）。
- `Start()` → 校验绑定 + 32 位 → `OnStart()` 起一个**后台循环** `StateMachineEngine.Run(loop)`。
- `Pause()/Stop()` 控制循环；`Stop()` 释放 runner。
- 暴露给 UI：`Name / Group / RunState / CurrentStateName / StateChanged 事件`。

→ **前端只需绑定这 5 个量**就能展示任意模式的运行态，不用关心内部。

### 1.2 九大服务（谁负责什么）
| 服务 | 职责 | 被谁用 |
|---|---|---|
| WindowBindingService | 拖拽绑定、句柄/PID/位数 | 全部 |
| WindowCaptureService | 客户区/区域/头像截图(GDI PrintWindow) | 识别层、绑定面板头像 |
| TemplateOcrService | 加载字库 + 点阵模板识别 | 所有识别层 |
| InputService | 发键/点击(双通道) + 公共CD | 所有控制器 |
| 各 RecognitionService | 截图→OCR→结构化状态 | 对应控制器 |
| 各 ConfigService | 该模式配置的读写/保存/加载 | 对应控制器 + 对应页面 |
| GlobalConfigService | 全局通用配置 + 门派联动 | 通用配置页 + 多个控制器 |
| StateMachineEngine | 后台循环承载(取消/异常隔离) | 所有控制器 |
| RuntimeLogService | 统一日志出口 | 全部 |

### 1.3 识别层（"读游戏"的唯一入口，最值得前端重视）
| 识别服务 | 读什么 | 配置 |
|---|---|---|
| CombatRecognitionService | 目标名 / 目标血% / 人物血% / 蓝% / 宠物血% | `CombatRecognitionConfig`（**5 个 OCR 区域**）|
| NavigationRecognitionService | 当前地图名 / 当前坐标(x,y) | `NavigationRecognitionConfig`（2 个区域）|
| DeathRecognitionService | 是否死亡（关键词匹配）| `DeathRecognitionConfig` |
| HorseBanditRecognitionService | 队伍/跟随状态、掉队、队长距离 | `HorseBanditRecognitionConfig` |
| ReturnNavigationService | 执行"走到目标点"的导航动作 | `ReturnNavigationConfig` |

`OcrRegionConfig{X,Y,W,H,Enabled}` + `OcrRegionProjection.Resolve()` 负责把区域按客户区实际分辨率缩放（右锚定参考宽度 2560，用于右上角小地图坐标）。

> ⚠️ **关键发现 1（前端必须决策）**：OCR 区域配置**分散且重复**。`CombatRecognitionConfig` 同时存在于 `GlobalConfig`（按键打怪、马贼用）**和** 各模式 config（抢NPC/反贼/和尚/三环各有一份）。也就是说同一套"目标血量"区域要在多处分别标定。**建议前端把 OCR 区域校准做成一个全局的"识别区域中心"，一次标定全局共享**，否则用户要在 6 个地方重复标定同一区域。

---

## 2. 配置体系：共享 vs 独立（前端信息架构的地基）

### 2.1 GlobalConfig（全局通用配置，所有模式共享）
代码：`Contracts/GlobalConfig.cs`，服务 `GlobalConfigService`。
| 分组 | 字段 |
|---|---|
| 门派 | `School`（峨眉→自动 `EnableEmHeal`，业务规则在服务里）|
| 输入 | `InputDispatchMode`（PostMessage/SendInput）|
| 通用开关 | `AutoReconnect / AutoLogin / ShoutEnabled / ShoutText` |
| 快捷键 | `Attack / Meditate / Mount / Heal / Mana / PetHeal` |
| 阈值 | `HpThresholdPct / MpThresholdPct / PetHpThresholdPct / PublicCooldownMs` |
| 峨眉加血 | `EnableEmHeal / EmHealHotkey / EmHealThresholdPct` |
| 技能预设 | `List<SkillPreset>`（名/键/间隔/启用/优先级/条件）|
| 目标预设 | `TargetingPreset`（筛选/扫描间隔/自动选怪/换怪延时）|
| 战斗识别 | `CombatRecognition`（5 区域）|
| 打码 | `Captcha` |

### 2.2 各模式独立 Config（互不影响）
| 模式 | Config 类型 | Config 服务 |
|---|---|---|
| 挂机返点 | `ReturnPointConfig` | ReturnPointService |
| 马贼模式 | `HorseBanditConfig` | HorseBanditConfigService |
| 抢 NPC | `NpcGrabConfig` | NpcGrabConfigService |
| 自动反贼 | `AutoRebelConfig` | AutoRebelConfigService |
| 自动和尚 | `AutoMonkConfig` | AutoMonkConfigService |
| 三环副本 | `DungeonConfig` | DungeonConfigService |
| 按键打怪 | （无独立 config，全用 GlobalConfig）| — |

### 2.3 持久化关系
- 每个 ConfigService 和 GlobalConfigService 各有自己的 `Save(profileName)/Load(profileName)`，**底层都写到 ProfileConfigService 的命名 JSON**。
- 左侧"配置方案 新建/保存/加载"目前**只调 `GlobalConfigService.Save/Load`**。

> ⚠️ **关键发现 2（前端必须决策）**：一个"命名方案"目前**只存了全局配置，没有把各模式独立配置一起存档**。要么前端"保存方案"时遍历调用所有 ConfigService.Save(同名)，要么把所有 config 聚合成一个总 Profile。否则用户存的方案不完整。

---

## 3. 逐功能拆解

> 每个功能给：作用 / 代码 / 状态机 / 关键配置 / 依赖 / 与其它功能的关系 / 完成度 / 前端交互要点。

### 3.1 按键打怪 `hang.normal`（挂机核心）
- **作用**：自动选怪→技能循环→打死换怪，含血蓝/宠物/峨眉加血。
- **代码**：`NormalHangController`（11KB，逻辑最完整）。配置全来自 `GlobalConfig`。
- **状态机**：`Idle→BindWindow→CheckRoleStatus→(循环) CheckHpMpPet→SelectTarget→AttackLoop→LootOrNextTarget→ErrorRecovery`。
- **逻辑要点**：
  - 每轮 `CombatRecognition` 读目标名/血/人物血蓝/宠物血。
  - 血蓝/宠物/峨眉：低于阈值且**OCR 读到数值**才发对应键（读不到数值不伪造触发）。
  - 技能：按 `Priority` 排序，各自 `IntervalMs` 节流，过 `PublicCooldownMs` 才发。
  - 目标策略：`只打红名/只打已标记` 当前**明确拒绝执行并告警**（缺颜色/标记识别），不会乱打。
- **关系**：直接吃 GlobalConfig 的技能/目标/阈值/识别。**不含返点**（见 3.2 的结构问题）。
- **完成度**：逻辑 Partial（结构完整，依赖 OCR 区域+字库现场校准）。
- **前端要点**：这页其实就是"通用配置"的执行入口；技能表/目标策略/阈值都在通用配置里，模式页基本只需"启动+状态"。

### 3.2 挂机返点 `hang.return`
- **作用**：识别当前坐标/死亡→导航回挂机点；支持 定点/土灵/死亡/卡点恢复。
- **代码**：`ReturnPointController`（14KB）+ `ReturnPointService`。
- **状态机**：`AutoRebelState` 风格的导航状态（校验→识别坐标→比距离→导航→到点）。
- **关键配置**（`ReturnPointConfig`）：`ReturnPointKind`(Normal/FixedFarm/EarthSpirit/DeathReturn/Recovery) + 多坐标列表 + 到点距离/卡点超时/重试 + 4 套 `ReturnNavigation` + 死亡识别。可 `ImportLegacyIni()` **直接导入马2.31 的 `自定义坐标.ini`**。
- **逻辑**：纯导航，**不打怪**。识别坐标→算到目标点距离→走过去。
- **完成度**：Partial。

> ⚠️ **关键发现 3（最重要的交互结构问题）**：UU 里"挂机返点"是"按键打怪"的**子能力**（边打边在偏离/死亡时回点）。但现在它被做成了一个**独立、互斥的模式**——而"单一主模式"规则下，**按键打怪 和 挂机返点不能同时运行**。结果："返点"单独跑只会把人送回点然后干站着；"按键打怪"单独跑死了不会回点。
> **前端架构建议**：把"返点/死亡返点/土灵返点"重构为**挂机模式内的开关与子配置**（与技能、阈值并列），而不是与按键打怪平级的独立 Tab/模式。这样才符合 UU 实际玩法，也避免互斥冲突。

### 3.3 马贼模式 `hang.horse`
- **作用**：队员跟随队长 + 找马/识别马贼/攻击/打死换目标/掉队恢复。
- **代码**：`HorseBanditModeController`（9.8KB）。
- **状态机**：`Idle→BindWindow→CheckClient32Bit→CheckTeamState→FollowLeader→SearchHorseBandit→SelectTarget→AttackTarget→WaitBattle→DetectKillOrLostTarget→Recover→ReturnToFollow→Stop`。
- **关键配置**（`HorseBanditConfig`）：`Role`(队长/队员) + 目标关键词 + 找/攻击/跟随热键 + 自动跟随(掉队距离/转圈拉跟随) + 句柄1/2(**仅作来源记录，不当运行绑定**) + 路线坐标。
- **依赖**：HorseBanditRecognition(队伍/跟随) + Combat(目标) + Input。战斗识别用的是 `GlobalConfig.CombatRecognition`（与按键打怪共享 OCR 区域）。
- **关系**：与按键打怪共享全局战斗识别；跟随逻辑独立。
- **完成度**：Partial。

### 3.4 三环副本 `hang.dungeon`
- **作用**：老三/新三环按脚本步骤执行→等战斗→确认完成→清包→衔接下个任务。
- **代码**：`DungeonController`（10.8KB）+ `DungeonConfig`。
- **关键配置**：`Mode`(老三/新三) + 次数 + 队伍编号 + 我去左桥 + 新三2 boss 站位 + 结束清包 + 完成衔接 + **步骤脚本 `List<DungeonScriptStep>`**(每步：地图/坐标/动作类型/热键/点击/延时) + 完成识别区域+关键词。
- **逻辑**：脚本驱动的步骤执行（HotkeyOnly/Click/WaitBattle/WaitCompletion）。
- **独立** `CombatRecognition`（不共享全局）。
- **完成度**：Partial。

### 3.5 抢 NPC `act.grab`
- **作用**：扫描→识别目标(9 类)→按优先级抢(发键/点击)→失败重试。
- **代码**：`NpcGrabController` + `NpcTargetingService`(选目标) + `NpcActionService`(执行动作)，**两服务拆分**符合要求。
- **关键配置**（`NpcGrabConfig`）：扫描/动作热键 + 扫描·点击·重试间隔/上限 + 动作模式(键/点击/键+点击) + 点击坐标 + 9 个 `NpcGrabTargetConfig`(枚举: 抢反/小/小丹/小2/和尚/箱子/上架/高甲卫/高甲卫23，各带启用/关键词/优先级)。
- **独立** `CombatRecognition`。
- **完成度**：Partial。
- **前端要点**：核心交互是"目标清单(启用+优先级+关键词)"+"动作方式"，建议做成可勾选可排序的目标表。

### 3.6 自动反贼 `act.rebel` / 3.7 自动和尚 `act.monk`
- **作用**：沿路线坐标巡逻→识别目标→进战→结束→异常恢复。两者结构几乎一致（共用活动状态机骨架，但**各自独立实例**）。
- **代码**：`AutoRebelController`(10KB) / `AutoMonkController`(11.5KB)。
- **状态机**：`Idle→ValidateConfig→DetectTarget→RecognizePosition→(导航 NavigateRoute)→Engage→Recover→Stop`。
- **关键配置**：路线坐标点 + 目标关键词 + 地图选择 + 路线导航开关 + 扫描/战斗间隔 + 独立 Combat/Navigation 识别。
- **逻辑**：每轮先看有没有目标→有就打；没有就按 Navigation 识别当前坐标→沿路线走→搜索。**活动自带战斗+导航**（不依赖按键打怪）。
- **关系**：与返点共用 `NavigationRecognitionService` + `ReturnNavigationService`（都是"识别坐标→走过去"）。
- **完成度**：Partial。
- **冲突**：产品文档要求自动反贼/和尚/副本/马贼**时间上互斥**（同窗口同时只一个主模式）。

---

## 4. 功能间关系总图

```
                       ┌──────────────── GlobalConfig（共享） ───────────────┐
                       │ 门派 / 技能预设 / 目标预设 / 阈值 / 公共CD / 6热键      │
                       │ CombatRecognition(5区域) ← 按键打怪、马贼 共用         │
                       └───────────────────────────────────────────────────┘
   按键打怪 ─使用→ GlobalConfig 全套
   马贼     ─使用→ GlobalConfig.CombatRecognition + 自己的 HorseBanditConfig
   抢NPC    ─使用→ 自己的 NpcGrabConfig(含独立 CombatRecognition)
   反贼/和尚 ─使用→ 自己的 Config(含独立 Combat + Navigation) + 共享 NavigationRecognitionService
   三环     ─使用→ 自己的 DungeonConfig(含独立 Combat + Navigation + 步骤脚本)
   返点     ─使用→ 自己的 ReturnPointConfig(Navigation + Death识别)；与按键打怪本应是子能力

   共享识别服务：CombatRecognition / Navigation / Death / ReturnNavigation（单例，被多控制器复用）
   共享执行：InputService(含全局公共CD) — 注意公共CD是全局单例，跨模式共享一个CD窗口
```

### 互斥与调度
- 当前：`MainViewModel.Start` 启动选中模式前，**手动 Stop 其它正在运行的控制器** → 实现了"单一主模式"。
- **缺失**：产品文档要的 `ActivityScheduler`（按时间窗自动切换反贼/和尚/副本、切换前安全收尾）**尚未实现**。现在是纯手动单选。

### 共享导致的耦合点（前端要注意）
1. **公共 CD 是全局单例**：所有模式共用一个 `InputService.TryConsumeGlobalCooldown` 窗口。同一时刻只跑一个模式时没问题；若将来要并行（如挂机+返点），CD 会互相抢。
2. **OCR 区域分散**（关键发现1）。
3. **方案保存不全**（关键发现2）。
4. **返点 vs 挂机的模式错位**（关键发现3）。

---

## 5. 状态机状态对照（前端可视化用）

| 模式 | 状态枚举 | 状态 |
|---|---|---|
| 按键打怪 | NormalHangState | Idle/BindWindow/CheckRoleStatus/SelectTarget/ValidateTarget/AttackLoop/CheckHpMpPet/LootOrNextTarget/ReturnPointIfNeeded/ErrorRecovery/Stop |
| 马贼 | HorseBanditState | Idle/BindWindow/CheckClient32Bit/CheckTeamState/FollowLeader/SearchHorseBandit/SelectTarget/AttackTarget/WaitBattle/DetectKillOrLostTarget/Recover/ReturnToFollow/Stop |
| 反贼/和尚 | AutoRebelState/AutoMonkState | Idle/ValidateConfig/DetectTarget/RecognizePosition/Navigate/Engage/Recover/Stop（各自独立）|
| 三环 | DungeonState | 步骤脚本驱动（进入/战斗/完成/清包/衔接）|
| 返点 | 导航状态 | 校验/识别坐标/比距离/导航/到点/恢复 |

---

## 6. 给前端交互架构的结论性建议

1. **以"识别区域中心"统一 OCR 标定**：把 5 个战斗区域 + 导航区域做成全局一次标定，所有模式引用，替代现在 6 处各存一份。
2. **"挂机"应是一个复合页，而非并列小模式**：按键打怪 + 返点/死亡返点/土灵返点 + 血蓝宠物 + 峨眉，应在同一"挂机"模式里用开关组合，返点不要做成独立互斥模式。
3. **方案(Profile)要聚合全部配置**：保存方案 = 全局 + 当前所有模式 config 一起存。
4. **活动类(反贼/和尚/三环/马贼)保持互斥单选**，并补 `ActivityScheduler` 做按时间自动切换（产品文档要求）。
5. **统一"模式页"骨架**：头部(名称/来源/状态机当前态/启动停止) + 中部(该模式独立配置) + 复用全局(技能/识别)。每个模式页结构一致，降低认知负担。
6. **运行态可视化**：所有模式都暴露 `RunState + CurrentStateName + StateChanged`，前端用统一组件展示状态机当前态即可。

---

> 本文档为只读梳理，未改动任何代码。如需我据此出一版"前端交互重构方案(IA + 线框)"，可在此基础上继续。
