# 龙伴行数据获取目标模式

日期：2026-07-01

## 1. 结论

龙伴行不把“官方在线客户端实时内存扫描/注入/写内存”作为产品主线。

原因不是技术上完全做不到，而是这条路会直接进入账号停权高风险区，并且会把产品架构绑死在易变指针、对象表和安全检测面上。后续产品主线采用三条可落地数据通道：

1. **运行态外部识别**：截图、OCR、图色、用户校准、日志。
2. **客户端静态数据**：本地 `Data/*.axp`、`ndata.dat`、Lua、XML、配置、旧授权数据、开源资料。
3. **采集样本库**：目标采集器保存的截图、标注、区域、模板和识别结果。

内存方向只保留在文档层作为“已放弃的高风险路径”和“离线研究备忘”，不进入可交付产品。

## 2. 本地只读扫描发现

### 2.1 官方客户端目录

根目录：`C:\Game\TLBB`

已确认存在：

- `C:\Game\TLBB\Bin\Game.exe`
- `C:\Game\TLBB\Bin64\`
- `C:\Game\TLBB\Data\Config.axp`
- `C:\Game\TLBB\Data\Scene.axp`
- `C:\Game\TLBB\Data\Model.axp`
- `C:\Game\TLBB\Data\Model2.axp`
- `C:\Game\TLBB\Data\Model3.axp`
- `C:\Game\TLBB\Data\Interface.axp`
- `C:\Game\TLBB\Data\Material*.axp`
- `C:\Game\TLBB\Data\Effect.axp`
- `C:\Game\TLBB\Data\Brushes.axp`
- `C:\Game\TLBB\Data\Sound.axp`
- `C:\Game\TLBB\Data\ndata.dat`
- `C:\Game\TLBB\Helper\HelperItems.xml`
- `C:\Game\TLBB\Helper\PaiHangItems.xml`
- `C:\Game\TLBB\Accounts\...\Acce_Custom.ini`
- `C:\Game\TLBB\ChatRecord\*.txt`

这些文件适合用于静态数据提取、物品/场景/资源索引、界面配置、快捷键配置和日志辅助分析。

### 2.2 外部数据源目录

根目录：`<EXTERNAL_DATA_ROOT>`

已确认存在大量 Lua，例如：

- `01-大漠自定义\AutoAttackSkill.lua`
- `01-大漠自定义\AutoAttack_Button.lua`
- `01-大漠自定义\AutoSearch.lua`
- `01-大漠自定义\AutoPickUp.lua`
- `01-大漠自定义\ActionSkill.lua`
- `01-大漠自定义\Bank.lua`
- `01-大漠自定义\BigBank.lua`
- `lua\lua-jd-20230731\AutoAttackSkill.lua`
- `lua\lua-jd-20230731\AutoSearch.lua`

扫描命中线索：

- `DataPool:GetNPCIDByServerID(...)`
- `this:CareObject(...)`
- `BeginCareObject(...)`
- `SetAutoRunTargetNPCName(...)`
- `ZDZD_AUTOSEARCHNPC`
- `OBJECT_CARED_EVENT`
- `Get_XParam_INT(...)`

这些是客户端 Lua/UI 层接口线索，适合整理成“客户端脚本知识库”，用于理解自动战斗、自动寻路、NPC 关注、NPC 对话、目标选择、界面状态，而不是直接当作外部程序的内存接口。

### 2.3 已有外部研究资料

项目内已有资料：

- `docs/runtime/external-research/AutoTLBB`
- `docs/runtime/external-research/TLBB_AXPPacker_DBEditor`
- `docs/runtime/external-research/TLBBModelLoader`
- `docs/runtime/external-research/sazima_tlbb2`

当前判断：

- `AutoTLBB` 有状态读取、CE 表、坐标/场景等研究价值，但其内存读取路径不进入产品主线。
- `TLBB_AXPPacker_DBEditor` 可作为 `.axp` 静态数据提取候选。
- `sazima_tlbb2` 提供 BMP/PNG 模板、门派图标、马贼/NPC/地图模板和外部操作流程参考。

## 3. 数据通道分级

### 3.1 绿色通道：进入产品主线

这些可以直接进入龙伴行主线：

1. **窗口截图**
   - 客户区截图。
   - 固定区域截图。
   - 头像/目标栏/小地图/背包/按钮区域截图。

2. **OCR / 图色 / 模板**
   - 血条、蓝条、怒气条。
   - 目标名、地图名、坐标。
   - 玩家目标判定。
   - 宠物技能槽、背包格子、按钮状态。

3. **客户端静态文件**
   - `.axp` 解包后的表、图片、地图、NPC、怪物、物品信息。
   - `ndata.dat` 可解析内容。
   - `HelperItems.xml`、`PaiHangItems.xml`。
   - Lua 脚本的 UI/API 语义。

4. **用户采集样本**
   - 玩家目标/怪物目标/首领目标截图。
   - 门派标识模板。
   - 宠物技能槽模板。
   - 背包格子和物品 tooltip 样本。
   - 抢 NPC 场景样本。

5. **旧授权数据静态资产**
   - 字库。
   - 坐标路线。
   - 题库。
   - 保留/存放/交易物品清单。
   - 旧配置字段。

### 3.2 黄色通道：只做离线研究，不进默认产品

这些只允许在离线样本、测试服、内部环境或明确隔离环境中研究：

1. `AutoTLBB` / CE 表里的指针链含义整理。
2. 旧客户端行为对照，例如功能名称、配置字段、状态机设计。
3. 离线客户端资源包结构分析。
4. Lua 事件、DataPool 调用和 UI 控件关系梳理。

输出形态必须是文档、静态映射或样本库，不输出在线进程读写代码。

### 3.3 红色通道：不进入龙伴行产品

以下不做：

1. 对官方在线 `Game.exe` 做 `ReadProcessMemory` 指针扫描。
2. 遍历在线客户端对象表获取玩家、怪物、NPC、坐标、目标状态。
3. 对在线客户端写内存、写坐标、改目标、改状态。
4. 注入 DLL、远线程、Hook 游戏或第三方安全组件。
5. Hook 旧工具或游戏的 `ReadProcessMemory / WriteProcessMemory` 来反推在线偏移。
6. 规避、干扰或绕过第三方安全/风控/反作弊。

## 4. 目标架构

### 4.1 `ClientStaticDataService`

职责：

- 扫描 `C:\Game\TLBB\Data`。
- 建立资源包索引。
- 记录文件大小、时间、哈希。
- 标记可解析/待解析/失败。

输出：

- `data/client-static/index.json`
- `data/client-static/source-files.json`

### 4.2 `AxpArchiveService`

职责：

- 调用或复用 AXP 工具解析 `.axp`。
- 优先尝试 `Config.axp`、`Scene.axp`、`Interface.axp`。
- 解出文件只放到龙伴行工作区，不修改原始客户端。

输出：

- `data/client-static/axp/config/`
- `data/client-static/axp/scene/`
- `data/client-static/axp/interface/`
- `docs/research/AxpExtractionReport.md`

### 4.3 `LuaKnowledgeService`

职责：

- 扫描 `<EXTERNAL_DATA_ROOT>` 中 Lua。
- 提取关键 API、事件、控件名、函数名、NPC/目标/自动战斗相关调用。
- 建立“Lua 语义索引”，辅助理解游戏 UI 和行为。

重点关键词：

- `AutoAttack`
- `AutoSearch`
- `AutoPickUp`
- `DataPool`
- `CareObject`
- `NPCID`
- `ServerID`
- `OBJECT_CARED_EVENT`
- `ZDZD_AUTOSEARCHNPC`
- `SetAutoRunTargetNPCName`

输出：

- `data/client-static/lua-index.json`
- `docs/research/LuaClientApiMap.md`

### 4.4 `SceneStaticDataService`

职责：

- 从静态资源中整理地图、场景、NPC、怪物、传送点、活动目标。
- 为抢 NPC、马贼、自动反贼、自动和尚、三环、挖矿、挖图提供静态库。

输出：

- `data/static/scenes.json`
- `data/static/npcs.json`
- `data/static/monsters.json`
- `data/static/activities.json`

### 4.5 `ItemStaticDataService`

职责：

- 从客户端文件、XML、旧清单、tooltip 样本中建立物品库。
- 支持保留、可卖、可存、可交易、未知保护。

输出：

- `data/static/items.json`
- `data/static/item-rules.json`

### 4.6 `RuntimeVisualStateService`

职责：

- 继续使用外部截图识别运行态信息。
- 读取人物状态、目标状态、宠物状态、地图坐标、背包状态。
- 不读进程内存。

当前可覆盖：

- 人物 HP/MP。
- 宠物 HP。
- 目标 HP。
- 目标是否疑似玩家。
- 目标是否可攻击。
- 小地图坐标。
- 背包格子。
- 宠物技能槽点击区域。

### 4.7 `SampleCorpusService`

职责：

- 管理目标采集器产生的截图和标注。
- 支持玩家、怪物、首领、NPC、门派、宠物技能、背包、按钮等标签。

输出：

- `data/samples/index.json`
- `data/samples/targets/`
- `data/samples/ui/`
- `data/samples/inventory/`

## 5. 功能落地路径

### 5.1 人物状态

主线方案：

- 截图识别角色头像区。
- 图色识别 HP/MP 条百分比。
- 死亡提示 OCR。
- 状态异常用日志和提示区域辅助判断。

不采用：

- 读人物对象内存。
- 读 Buff 数组。
- 写角色坐标。

### 5.2 目标状态

主线方案：

- 截图识别目标栏。
- 血条数量/颜色判断目标类型：
  - 怪物：通常只有血条，部分首领/BOSS 有多条。
  - 玩家：头像、门派标识、蓝条/怒气条等 UI 特征。
- 目标名 OCR/模板作为辅助。
- 默认不攻击玩家目标。

不采用：

- 读目标对象指针。
- 遍历对象表。
- 写当前目标。

### 5.3 地图和坐标

主线方案：

- 小地图右上角文字/数字区域截图。
- 右锚定区域投影，适配 1920/2560 等宽度。
- 坐标使用数字图色模板和 OCR 双通道。
- 地图名用 OCR，识别失败时不跨地图执行高风险回点。

下一步需要修正：

- 安全回点默认要求地图识别成功，或者用户锁定当前地图。
- 地图未知时只允许提示和暂停，不执行跨地图导航。

### 5.4 物品数据库

主线方案：

1. 先解析静态资源：
   - `HelperItems.xml`
   - `PaiHangItems.xml`
   - `.axp` 解包后的表。
2. 再补用户 tooltip 采集：
   - 鼠标悬停背包格。
   - OCR tooltip 名称。
   - 用户标注保留/出售/存放。
3. 清包默认只 DryRun。

不采用：

- 读背包对象数组内存。
- 读物品 ID 指针。
- 自动出售未知物品。

### 5.5 抢 NPC / 抢小 / 抢反

低风险主线方案：

- 静态刷新点库。
- 地图/坐标校准。
- 目标名/目标框/屏幕文字/OCR。
- 客户区点击和交互键。
- 活动前预站位、刷新窗口扫描、失败重试。

高风险路径不进入主线：

- 读在线对象表抢刷新。
- 内存调用打开 NPC。
- 写目标或交互 CALL。

## 6. 验收标准

### 6.1 数据资产验收

- 能生成 `client-static/index.json`。
- 能列出所有 `.axp`、`.dat`、`.xml`、Lua 资源。
- 能输出可解析/不可解析报告。
- 不修改 `C:\Game\TLBB` 原始文件。

### 6.2 AXP 静态解析验收

- 至少验证 `Config.axp`、`Scene.axp`、`Interface.axp` 是否能解。
- 能生成解包清单。
- 能识别其中是否有地图、NPC、怪物、物品、界面图片。
- 失败时记录错误，不阻塞主程序。

### 6.3 Lua 知识库验收

- 能扫描 Lua 文件数量。
- 能提取函数名、事件名、DataPool 调用、NPC 相关调用。
- 能输出 `LuaClientApiMap.md`。
- 能明确哪些是 UI 层信息，哪些不能直接外部调用。

### 6.4 运行态外部识别验收

- 人物 HP/MP 识别稳定。
- 目标 HP 和玩家目标阻断可验证。
- 小地图坐标在 1920/2560 下可校准。
- 背包网格能在默认位置识别为 `10x6`。
- 地图未知时不会执行跨地图回点。

## 7. 第一批任务清单

1. 新建 `ClientStaticDataService`，扫描 `C:\Game\TLBB\Data` 和 `Helper`。
2. 生成 `data/client-static/index.json`。
3. 试跑 AXP 解包工具，只读复制到工作区。
4. 生成 `docs/research/AxpExtractionReport.md`。
5. 新建 `LuaKnowledgeService`。
6. 扫描 `<EXTERNAL_DATA_ROOT>` 的 Lua。
7. 生成 `docs/research/LuaClientApiMap.md`。
8. 把安全回点改成“地图未知不跨地图执行”。
9. 把物品库主线改成“静态库 + tooltip 采集 + 用户规则”。
10. 给抢 NPC 建立“低风险主线”：刷新点 + 外部识别 + 快速点击，不接入对象表内存读取。

