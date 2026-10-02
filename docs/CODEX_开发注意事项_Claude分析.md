# 给 CODEX 的开发注意事项（Claude 分析版）

> **性质声明**：本文是 Claude 在 2026-06 对当前代码的一次**只读分析与建议**，源于与开发组的沟通。
> **它不是命令**。CODEX 请结合代码现状与自身判断决定采纳哪些、如何实现，不必照单全收。
> 凡标【建议】的为 Claude 观点；标【需求】的为开发组明确要求；标【事实】的为代码实测结论。

参考详档：[功能运作地图](modules/BlueDiamond_FunctionLogicMap.md)、[复用计划](migration/BlueDiamond_ReusePlan.md)、[去登录计划](migration/BlueDiamond_AuthRemovalPlan.md)、[ADR-001 技术栈](architecture/ADR-001-TechnologyStack.md)。

---

## 1. 现状总评（Claude 视角）

【事实】当前代码**方向正确、架构边界守得住、UU 主体功能忠实覆盖且状态标注诚实**（P0=Partial，P1=NotIntegrated，未伪装完成）。后端不是占位：TemplateOcrService 是真字库模板匹配、InputService 是 PostMessage/SendInput 双通道、各 Controller 是真实独立状态机。**不需要推倒重来**。

下面是"该保留的"和"待决策的"，请 CODEX 据此判断。

---

## 2. 已确认"做得对"的地方（请勿误删/误重写）

- 分层：UI→Contracts，Core 零 WPF 引用；WinAPI 只在 Core。继续保持。
- 控制器统一骨架 `AutomationControllerBase<TState>` + 独立 `StateMachine` + `StateMachineEngine` 后台循环。新模式照此模板加。
- 马贼独立 `HorseBanditModeController`；抢 NPC 拆 `NpcTargetingService`+`NpcActionService`；反贼/和尚/三环各自独立状态机。
- `NormalHangController` 对做不到的明确拒绝（只打红名/已标记→不攻击并告警；血蓝无 OCR 数值→不伪造触发）。**这种"不造假"的姿态请保留**，不要为了"看起来能跑"而强行触发。
- 旧授权字段（如马贼 `句柄1/2`）仅作来源记录、不当运行绑定——符合去登录边界，保持。

---

## 3. 待决策的结构性问题（Claude 建议 + 理由，CODEX 定夺）

### 3.1 OCR 识别区域：分清"真冗余"与"真不同"
【事实】`CombatRecognitionConfig` 的 5 个区域（目标名/目标血/人物血/人物蓝/宠物血）在 GlobalConfig 和抢NPC/反贼/和尚/三环 config 里**各存一份，默认值完全相同**。这 5 个是天龙界面**固定 UI 位置**，不随模式变。
【事实】真正因模式不同的是：① 各模块**匹配关键词**（抢反/抢小 vs 反贼 vs 怪名）② **辅助识别区**（导航地图/坐标、死亡提示、副本完成）——这些读不同内容/不同位置，本就该独立。
【建议】把那 5 个战斗区域抽成**全局"识别区域中心"，标定一次全局共享**；各模式只保留自己的关键词与专属辅助区。消除冗余的同时尊重模式差异。
（注：数据调试页已有"战斗 OCR 校准"+区域预览，可作为这个中心的载体。）

### 3.2 命名方案(Profile)未打包，且方案列表被污染（已实测核实）
【事实】机制是这样的（核实过 ProfileConfigService / GlobalConfigService / 各模式 ConfigService / 各页 VM）：
- 各模式**会**持久化：每个模式页有自己的"保存"命令，调各自 `ConfigService.Save(名字)`。
- 各模式用**前缀文件**隔离存储：抢NPC→`npc-grab-{名字}.json`、马贼→`horse-bandit-...` 等，**不覆盖**全局的 `{名字}.json`。
- 左侧"保存方案"**只**调 `GlobalConfigService.Save` → **只写全局配置一个文件**，不触发任何模式保存。
- 键名可能不同步：侧栏用侧栏方案名；各模式用自己页面的 `ProfileName`（如"默认抢NPC"），二者常不是同一个名字。
- **Bug**：`ProfileConfigService.ListProfileNames()` 返回 profiles 目录下**所有** `*.json`（含前缀文件），导致侧栏方案下拉框里混进 `npc-grab-默认抢NPC` 这类**模式文件冒充成方案**。
【更正】不是"各模式配置没入档"（它们有入档），而是**没有"一个方案打包全部配置"的概念**，且方案列表被前缀文件污染。
【建议】二选一：① 侧栏"保存/加载方案"时**扇出**到全局 + 所有模式 ConfigService（同名）；或 ② 把全局+各模式 config 聚合成**一个总 Profile 对象**整体存一个文件。无论哪种，`ListProfileNames` 要**过滤掉前缀文件**，只列真正的方案。

### 3.3 挂机返点不应是独立互斥模式（Claude 认为最该改）
【事实】UU 里"挂机返点"是"按键打怪"的**子能力**（边打边在偏离/死亡时回点）；现在被做成独立模式，而"单一主模式"规则下二者**不能同时跑**——返点单独跑只会送回点干站着，打怪单独跑死了不回点。
【建议】把返点/死亡返点/土灵返点重构为**"挂机"模式内的开关与子配置**（与技能、阈值并列），不要做成平级独立 Tab。
【判断权交 CODEX】若 CODEX 有更好的组合方式（如"挂机=打怪Controller内组合返点子状态"），自行决定。

### 3.4 缺 ActivityScheduler
【事实】产品文档要的"同窗口同时只一个主模式、按时间自动切换、切换前安全收尾"目前只做到**手动单选**，自动调度未实现。
【建议】活动类（反贼/和尚/三环/马贼）补一个 `ActivityScheduler`。优先级不高，可排后。

### 3.5 公共 CD 是全局单例（并行隐患）
【事实】`InputService.TryConsumeGlobalCooldown` 是全局一个 CD 窗口。单模式没问题；一旦未来要并行（如挂机+返点），多路会互抢同一个 CD。
【建议】若引入并行，公共 CD 需按窗口/按通道隔离。

---

## 4. 【重要修正】UU 是两套独立机制：按键打怪=纯按键，挂机返点=内挂

> 本节据开发组用 UU 原版截图当面纠正后**重写**，替代旧版"双战斗后端/内挂用于野外"的说法。

### 4.1 UU 的真实分工（按原版 UI 实证）
| UU 标签 | 机制 | 内容 |
|---|---|---|
| **按键打怪** | **纯按键（外部，无内挂、无识别）** | 自动打怪、峨眉加血、打死换怪、**技能间隔(秒) F1-F7**、只打红怪、打红名人。本质=**按间隔轮流发键**，不读内存、不做 OCR。 |
| **挂机返点** | **内挂（读内存）** | **检测杀怪数**（界面里 `2c20` 是内存值）、死亡回点、土灵返点、`大于X→下号/回城`、分钟返点。这些必须**读内存**。 |

→ "直接复用 UU 按键打怪" = **就是一个定时发键循环**，不需要 OCR、不需要内挂。

### 4.2 当前实现的问题（这是 bug 多的根因之一）
【事实】`NormalHangController`（按键打怪）被做成了**依赖 OCR 识别的复杂状态机**：每轮 `_backend.Read` 走字库识别目标名/血量，且默认技能条件是 `TargetExists`（有目标）。**OCR 没识别到目标→技能不发**，于是必须先加载字库+标定区域，否则只发 F1、不发 F2-F7。
【判断】这是**把简单的事做复杂了**——UU 按键打怪从不识别，就是 F1-F7 按秒发。识别层是大量 bug（线程/校准/字库）的来源。

### 4.3 建议架构：ICombatBackend 三后端，对齐 UU 分工（CODEX 可改）
```
interface ICombatBackend
  ├─ CombatSnapshot Read(window)      // 可返回"未知"(纯按键后端就返回空快照)
  ├─ bool CastHotkey(window, hotkey)  // 发键/普攻/选怪
  └─ bool IsAvailable(window)

实现：
  ├─ PureKeyCombatBackend      ← 【按键打怪默认】不Read不识别：循环发 普攻 + 各技能按间隔 + 峨眉加血(定时/可选血量) + 打死换怪(定时发选怪键)
  ├─ OcrCombatBackend          ← 【可选增强】要"只打红怪/按血量加血"才开，需字库+校准；没有它按键打怪照样跑
  └─ InternalPluginCombatBackend ← 【挂机返点用】读杀怪数/死亡/坐标 → 返点/土灵/死亡回点/下号回城
```
- **按键打怪默认走 PureKey**：技能默认条件改为 `始终(定时)`，不要求"有目标"。这才是"复用 UU 按键打怪"。
- OCR 仅作可选增强；纯按键不依赖它。
- **内挂归属挂机返点**（不是"野外打怪"）：杀怪数/死亡/下号回城等内存特性。
- UI「战斗方式」可保留：纯按键 / 外部识别 / 内挂(返点)；默认纯按键。

### 4.4 内挂（挂机返点）的工程要点与风险（CODEX 评估后再实现）
- 【边界】内挂属**蓝钻自有**模块范畴（旧 uu `C:\sys\az.dll`/`dz.dll` 一类自有插件链）。仅在开发组自有授权内实现；**不碰第三方反作弊本身**。
- 【约束】32 位链：执行进程必须 x86，与 32 位 `Game.exe` 同位；不硬转 64 位。
- 【约束】内存地址/偏移（如杀怪数 `2c20`）**独立成可热更的偏移 profile**，不写死进通用配置；版本变了要能改。
- 【风险】读内存/注入是反作弊检测面，封号风险高于纯按键——UI/文档要明确告知用户，由用户选择。
- 【顺序】先把 `PureKeyCombatBackend` 落好让按键打怪稳定可用，OCR 增强次之，内挂(返点)作单独里程碑。

### 4.5 立即建议（解决"按键打怪需要识别才发技能"）
最小改动即可让按键打怪回归"纯按键、直接可用"：把按键打怪的技能默认条件设为 `始终`、并提供 PureKey 后端使其**不调用 Read/OCR**。这样不加载字库也能 F1-F7 正常轮发，符合 UU。

---

## 5. 不变的边界（提醒）

1. 只处理蓝钻自有软件/模块/授权数据；**不绕过第三方游戏/平台/验证码/反作弊/风控**。
2. 不直接 patch 原始样本，不改 `<LEGACY_DATA_ROOT>` 与 `C:\sys` 原始文件。
3. 旧登录认证只移除蓝钻自有的；不碰第三方账号体系。
4. 复用旧数据要留来源路径与处理说明。
5. 不伪装未完成功能为已完成（继续保持现在这种诚实状态标注）。

---

## 6. Claude 已产出的参考文档
- `docs/modules/BlueDiamond_FunctionLogicMap.md` — 每个功能的逻辑/代码/配置/状态/相互关系（本次分析核心）
- `docs/research/BlueDiamond_SourceInventory.md` / `BlueDiamond_ModuleComparison.md` — 授权数据资产与择优
- `docs/migration/BlueDiamond_AuthRemovalPlan.md` / `BlueDiamond_ReusePlan.md`
- `docs/architecture/ADR-001-TechnologyStack.md` / `BlueDiamond_32bit_First_Roadmap.md` / `TLBB_Agent_ProductScope.md`

> 再次声明：以上 Claude 的建议供参考，CODEX 按自身判断决定取舍与实现方式。
