# ADR-001：技术栈选型

> Architecture Decision Record · 状态：**已接受 (Accepted)**
> 日期：2026-06-20
> 决策者：TLBB Agent 架构（Claude Code 提案，待开发组确认）

---

## 1. 背景与决策驱动

TLBB Agent 第一阶段是 **32 位 Windows 桌面自动化客户端**。核心能力高度依赖：

- WinAPI：窗口枚举/句柄/PID/进程路径、窗口位数判定
- 窗口截图（GDI/PrintWindow/DWM）
- 按键与鼠标注入（PostMessage / SendInput）
- 本地文件：INI / TXT 字库 / BMP 模板 / 加密 DB
- **与 32 位 `Game.exe` 同位运行的执行层**（注入/部分内存访问需 x86）
- 固定字库点阵识别（CPU 密集，需高效位运算）

约束（产品文档 §13）：
1. 目标环境 Windows 桌面；2. 一期优先 32 位游戏客户端；3. 需同位 32 位能力时执行层须 x86；4. UI 与执行层解耦；5. 即使 UI 现代化，核心执行也应通过 `AutomationWorker32` 或等价模块运行。

---

## 2. 候选方案对比

| 维度 | A) .NET 8 + WPF/WinUI 3 | B) Tauri + React/TS | C) Electron + React/TS |
|---|---|---|---|
| WinAPI/句柄/截图/按键 | 🟢 原生 P/Invoke，直达 | 🔴 须经 Rust CoreBridge 全部桥接 | 🔴 须经 Node native/addon 桥接 |
| 与 32 位执行层集成 | 🟢 可直接发布 x86 / 起 x86 worker | 🟡 Rust 侧 x86 编译可行但桥接复杂 | 🟡 native addon x86，链路长 |
| 字库点阵识别性能 | 🟢 托管/unsafe/SIMD 直接 | 🟡 Rust 强但需大量 FFI 往返 | 🔴 JS 端弱，须落 native |
| 本地文件/INI/BMP | 🟢 成熟库 | 🟡 | 🟡 |
| 多窗口绑定/托盘/拖拽准星 | 🟢 原生 | 🟡 | 🟡 |
| 前端现代化/组件化 | 🟡 需现代 UI 库 | 🟢 强 | 🟢 强 |
| 与旧逻辑(WinAPI 重写)适配成本 | 🟢 最低 | 🔴 高 | 🔴 高 |
| 体积/资源占用 | 🟢 小 | 🟢 小 | 🔴 大 |
| 团队上手/迭代速度(本项目语境) | 🟢 | 🟡 | 🟡 |

**判断**：本项目底层能力 ≈ 90% 是 WinAPI/句柄/截图/按键/DLL/INI/本地文件/位运算识别。产品文档明确提示「如果底层逻辑大量依赖 WinAPI…优先考虑 .NET 桌面方案」。Tauri/React 的现代 UI 优势无法抵消其 CoreBridge 桥接成本与识别性能往返开销。

---

## 2.5 阶段定位与前端可替换性（开发组补充约束）

**当前阶段采用 .NET 8 + WPF 快速原型路线**，目标是先把 32 位稳定功能跑通——它是**功能验证型原型，不代表最终前端形态**。

架构上**保留后续升级为 `WPF Shell + WebView2 + React` 的能力**：
- 全部核心能力位于 **Core / Contracts**（UI 无关，零 WPF 引用），UI 只经 **ViewModel → 服务接口** 调用。
- 未来 React 前端通过一个 **CoreBridge**（WebView2 `postMessage` ↔ 同一批 Contracts 服务）接入，**复用现有服务层，不重写**。WPF 的 ViewModel 与 React 的状态层是同一组服务的两个可替换前端。
- 因此本阶段的硬约束是：**服务层稳定、UI 可丢弃**。

> 原则落地：**原型可以简陋，架构不能简陋；UI 可以后面换，服务层不能重写；按钮可以先简单，状态机必须独立。**

### 架构边界（强制，适用于 WPF 原型与未来任何前端）

1. UI 层只负责展示、输入、按钮命令、状态绑定。
2. Window / Page / UserControl 的 code-behind **不写**核心业务逻辑（仅窗口 chrome 与"取屏幕坐标→交 ViewModel"这类纯 UI 输入）。
3. 按钮事件**不直接写**窗口绑定/截图/OCR/按键/状态机/配置保存/导入数据导入。
4. 全部核心能力进服务层：AppInitService、WindowBindingService、WindowCaptureService、TemplateOcrService、InputService、StateMachineEngine、ProfileConfigService、RuntimeLogService、LegacyDataAdapter。
5. 马贼独立为 `HorseBanditModeController`。
6. 抢 NPC 拆为 `NpcTargetingService` + `NpcActionService`。
7. 自动反贼 / 自动和尚 / 自动三环为**各自独立状态机**，不与普通挂机混成一个大循环。
8. 配置只经 `ProfileConfigService` 读写，**不**用 UI 控件状态做持久化。
9. 日志只经 `RuntimeLogService` 输出。
10. 页面经 ViewModel 调服务，**不**直接调 WinAPI。
11. WPF 原型必须保留替换为 React/WebView2 前端的可能性。
12. 本阶段不追求像素级现代 UI，优先把 32 位稳定功能跑通；但 UI 信息架构按最终设计来：左侧绑定区 + 右侧 Tab + 底部日志 + 配置方案 + 状态机展示。

### 当前代码对边界的落实（v0.1 骨架）

| 边界 | 落实位置 |
|---|---|
| code-behind 无业务逻辑 | `MainWindow.xaml.cs` 仅 chrome + `PointToScreen` 后交 `BindingPanelViewModel` |
| 9 大服务齐备 | `TlbbAgent.Core`（截图/OCR/输入/StateMachineEngine/LegacyDataAdapter 为 stub，接口完整） |
| 马贼独立 | `HorseBanditModeController` + `HorseBanditState` 独立状态机 |
| 抢 NPC 拆分 | `INpcTargetingService` + `INpcActionService`，`NpcGrabController` 编排 |
| 反贼/和尚/三环独立 | `AutoRebelController` / `AutoMonkController` / `DungeonController` 各自 `StateMachine<ActivityState>` 实例 |
| 配置走服务 | `ProfileConfigService`（JSON），UI 只通过命令调用 |
| 日志走服务 | `RuntimeLogService`，全工程统一出口 |
| UI→服务经 VM | 全部 ViewModel 仅依赖 Contracts 接口；Core 零 WPF 引用 |
| 状态机展示 | 概览页 `StateMachines` 列表绑定 `IAutomationRegistry` |

---

## 3. 决策

**采用方案 A：.NET 8 + WPF（功能验证原型）。**

具体：

1. **语言/运行时**：C# / .NET 8（LTS）。
2. **UI 框架**：**WPF**（而非 WinUI 3）。理由：WPF 对 x86 发布、传统窗口互操作（HwndHost、拖拽准星、全局热键、多窗口）最成熟稳定；WinUI 3 的打包/x86/互操作仍有摩擦，不利一期稳定目标。
3. **UI 现代化**：引入现代 WPF UI 库（如 **WPF-UI (Fluent)** 或 HandyControl），**杜绝 Win98 观感**（产品文档 §11/§十）。卡片、表格、抽屉、步骤条、托盘。
4. **进程模型（关键）**：目标架构 = 产品文档**路线 B**（现代 UI Shell + `AutomationWorker32`）。
   - **一期落地策略**：MVP 可先单进程 **x86** 发布（路线 A 的速度），但**代码必须按 Worker 边界设计**：UI 层只发命令/收事件，自动化执行全部在 `Core Automation` 层后面，经 `AutomationCommandBus`/`AutomationEventBus` 通信。
   - 这样当需要把执行层拆为独立 x86 worker 进程（或未来加 `AutomationWorker64`）时，只换传输实现（进程内 → IPC），不改业务代码。
   - 传输抽象：一期用进程内 in-memory bus；预留 IPC 实现（Named Pipe / gRPC over localhost）。
5. **发布目标**：一期 `win-x86`（RID `win-x86`），确保与 32 位 `Game.exe` 同位互操作能力可用。
6. **CoreBridge 规则不适用本方案**，但等价的"UI 不写底层"约束仍强制执行：UI 层禁止直接写自动化大循环、禁止直接 P/Invoke 业务逻辑，一律经 Core/Adapter 层（产品文档 §18.4）。

---

## 4. 解决方案结构（落地映射产品文档 §8 分层）

```
TLBB Agent (solution, .NET 8)
├─ TlbbAgent.App            (WPF, x86, UI Layer)          UI 只发命令/订阅事件
├─ TlbbAgent.Application    (ProfileManager/ActivityScheduler/CommandBus/EventBus)
├─ TlbbAgent.Core           (Core Automation Layer)
│   ├─ Controllers          NormalHang/HorseBandit/Dungeon/NpcGrab/AutoRebel/AutoMonk/Life/TaskPipeline
│   └─ StateMachine         StateMachineEngine
├─ TlbbAgent.Adapters       (Adapter Layer)
│   ├─ ClientAdapter        IClientAdapter → Client32Adapter(一期唯一)
│   ├─ WindowBinding / Capture / Input / TemplateOcr / Config
│   └─ Legacy*Adapter       LegacyDataAdapter / 各模块导入
├─ TlbbAgent.Data           (Data Layer: profiles/templates/ocr/routes/task-data/question-bank/logs)
└─ TlbbAgent.Contracts      (共享 DTO/接口，为 Worker 拆分预留)
```

服务映射（产品文档 §七 9 大服务 → 工程）：
AppInitService/WindowBindingService/WindowCaptureService/TemplateOcrService/InputService → Adapters；StateMachineEngine/各 Controller → Core；ProfileConfigService → Application；RuntimeLogService → 横切；LegacyDataAdapter → Adapters。

---

## 5. 关键技术选型补充

| 关注点 | 选择 |
|---|---|
| 窗口/进程位数判定 | `IsWow64Process2` / `GetProcessImageFileName` P/Invoke |
| 截图 | `PrintWindow`(支持后台/被遮挡) 优先，回退 BitBlt |
| 按键/点击 | `PostMessage`(后台) + `SendInput`(前台)双通道，可配置 |
| 字库识别 | 自研点阵匹配器（Span<byte>/位运算），吃 aj/zk/001 两种格式 |
| 配置 | JSON(System.Text.Json) 为主；导入旧 INI 用专用 importer |
| 日志 | 自研 RuntimeLogService（结构化：时间/窗口/模块/等级/内容）+ 可选 Serilog sink |
| DB(题库) | 一期 JSON/SQLite(Microsoft.Data.Sqlite)；bmp.db 解密单列调研 |
| 安全账号存储 | DPAPI（替代旧明文 账号.txt） |
| 打包 | 一期 framework-dependent 或 self-contained x86，单文件可选 |

---

## 6. 后果

**正面**：底层适配成本最低、识别性能最佳、x86 同位互操作直接、体积小、与旧 WinAPI 逻辑重写对齐、Worker 边界保证可演进到 64 位。

**负面 / 代价**：
- WPF 现代 UI 需引入第三方 UI 库并投入样式工作（可接受）。
- 一期单进程 x86 若未来 UI 想升 x64，需把执行层拆 worker——**已通过 Bus 边界预先规避**。
- 放弃 Web 前端生态的组件红利——本项目以桌面互操作为重，权衡后接受。

**被否决**：
- 方案 B/C：CoreBridge/native 桥接成本高、识别性能往返开销大、与"大量 WinAPI 重写"目标不匹配（产品文档明确不要盲目选 Web 前端）。

---

## 7. 待确认（提交开发组）

1. UI 库二选一：WPF-UI(Fluent) vs HandyControl。
2. 一期进程模型：单进程 x86（更快）确认；是否需要一期即拆 worker（更稳但更慢）。
3. 发布形态：framework-dependent vs self-contained 单文件。

> 默认按本 ADR 推进：.NET 8 + WPF + WPF-UI，一期单 x86 进程、Worker 边界代码结构。
