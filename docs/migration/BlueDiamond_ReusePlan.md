# 蓝钻可复用数据与逻辑计划 (BlueDiamond_ReusePlan)

> 迁移阶段文档 · 列出可复用资产、来源、格式、接入目标、风险
> 生成日期：2026-06-20
> 状态：计划（未执行提取；提取须先复制到 `data/imported` 并留痕）

---

## 0. 复用总原则

- 复用对象 = **数据 + 流程蓝本**，逻辑一律在新后端用 .NET 重写，经 Adapter 接入。
- 不直接加载旧 `xx.dll`/`wm.dll` 执行业务（见 AuthRemovalPlan §4）。
- 每项提取必须记录：来源路径、来源模块、格式、可复用程度、目标格式、接入模块、风险。
- 提取产物落到 `data/imported/`，并写 `data/imported/index.json`（产品文档 §10.3）。

可复用程度图例：🟢直接可用 / 🟡需转换 / 🔴待解密或待运行态确认

---

## 1. 字库 / OCR / 模板类

| 项 | 来源路径 | 来源模块 | 格式 | 可复用 | 目标格式 | 接入模块 | 风险 |
|---|---|---|---|---|---|---|---|
| uu 核心字库 | `C:\sys\aj.txt` | uu(运行态) | 点阵$词$x.y.w$h | 🟢 | 统一 TemplateDict(JSON+点阵) | TemplateOcrService | 运行态产物，需复制不改原文件 |
| 字库(马) | `马2.31\bmp\zk.txt` | 马2.31 | 同 aj | 🟢 | 同上 | TemplateOcrService | - |
| 字库(队员马) | `队员马\bmp\zk.txt` | 队员马 | 同 aj | 🟢 | 同上 | TemplateOcrService | - |
| 字库(挖矿) | `挖矿2.15\bmp\zk.txt` | 挖矿 | 同 aj | 🟢 | 同上 | TemplateOcrService | - |
| 字库(师门) | `单师门\bmp\001.txt` | 单师门 | 词$w-h-?$点阵 | 🟢 | 同上(兼容字段顺序) | TemplateOcrService | 字段顺序不同，解析器需兼容 |
| 字库(挖图) | `挖图\bmp\001.txt` | 挖图 | 同 001 | 🟢 | 同上 | TemplateOcrService | - |
| 数字字库 | `*/bmp/数字.txt`,`数字1.txt` | 马/队员马/挖矿 | 点阵$数字 | 🟢 | 数字识别集 | TemplateOcrService | - |
| 副本/NPC 字库 | `生活王/*/图_pic/fuben.txt` | 生活王 | 点阵$姓名 | 🟢 | NPC 识别集 | TemplateOcrService/副本 | - |
| 图色模板 | `*/bmp/*.bmp`(8千+张) | 各模块 | BMP | 🟡 | 分类索引+模板库 | TemplateOcrService | 需逐张归类用途(怪/NPC/按钮/状态) |
| OCR 识别词 | aj/zk 内识别词 | uu/马 | 文本词条 | 🟢 | 识别词字典 | TemplateOcrService | - |

**怪物/BOSS/NPC/地图/状态名称**：内含于上述字库识别词（如 `坐骑状态/开启/完成/满` 及 fuben 人名、自定义坐标的地图名）。统一在解析字库时抽出名称集合。

---

## 2. 坐标 / 路线类

| 项 | 来源路径 | 来源模块 | 格式 | 可复用 | 目标格式 | 接入模块 | 风险 |
|---|---|---|---|---|---|---|---|
| 多地图巡逻坐标 | `马2.31\自定义坐标.ini` | 马2.31 | `地图=x,y/x,y/...` ×20+图 | 🟢 | RoutePoint[] per map | 挂机返点/马贼巡逻 | 坐标系=游戏小地图坐标，需校准 |
| 采集坐标(按图) | `生活王/*/挖矿采药自填坐标/<图>.txt` | 生活王 | 每行 `x,y` | 🟢 | RoutePoint[] | 挖矿/采药 | 多数图为空模板，仅无量山等有数据 |
| 副本坐标 | `生活王/*/图_pic/无量山.txt` | 生活王 | 坐标/标记 | 🟡 | RoutePoint[] | 副本/采集 | 需确认字段含义 |
| 跑商路线 | 马系列 config `[跑商]跑商路线` | 马/队员马 | `城市-城市-...` 字符串 | 🟢 | TradeRoute | 跑商 | 完整买卖点疑在跑商OL内嵌 🔴 |
| 挂机点/坐标 | config `挂机地图/挂机坐标` | 马系列 | `地图`+`x,y` | 🟢 | HangPoint | 挂机返点 | - |
| 集合坐标 | 生活王 pz.ini `集合坐标X/Y` | 生活王 | x,y | 🟢 | Point | 采集集合 | - |

---

## 3. 配置 / 参数类（→ Profile schema 蓝本）

| 项 | 来源 | 字段示例 | 可复用 | 目标 | 接入 | 风险 |
|---|---|---|---|---|---|---|
| 快捷键映射 | config `[界面]` | 坐骑/隐身/普攻/打坐/回血/治疗快键 | 🟢 | SkillHotkeyConfig | InputService/技能 | 旧键值=游戏快捷键号，需映射 |
| 技能槽位 | config `[技能]技能F1-F4` | F1→槽0... | 🟢 | 技能栏映射 | 按键打怪 | - |
| 血蓝阈值 | config `人物回血/宠物回血/治疗血/治疗蓝/回血1` | 数值 | 🟢 | NormalHangProfile | 挂机 | - |
| 技能间隔 | 生活王 pz.ini `F1-F5时间间隔` | ms | 🟢 | intervalMs | 技能节奏 | - |
| 返点配置 | config 死亡/挂机相关 | - | 🟡 | ReturnPointProfile | 返点 | uu 返点细节需运行态补全 |
| 死亡处理 | config/uu | 停止/返点/重挂 | 🟡 | 死亡策略枚举 | 返点 | - |
| 自动重连 | uu | - | 🔴 | 重连策略 | 左侧通用区 | 运行态确认 |
| 清包/随身银行/随身商店/御龙帖 | uu + config `存钱庄/清理背包/兑换银票/背包空位` | 布尔/数值 | 🟡 | 便捷动作 | 挂机便捷 | uu 具体动作待运行态 |
| 队员跟随参数 | 队员马 config | 掉队距离/转圈拉跟随/句柄1/2/同意进队 | 🟢 | FollowProfile | 马贼/双开 | 句柄=运行态绑定，不存死值 |
| 峨眉加血 | 队员马 config `[峨眉]` | 加血/加血量/冲虚/快捷键 | 🟢 | EmHealConfig | 峨眉加血 | - |
| 保留/存放/交易物品 | 马2.31`保留物品.txt`/单师门`存放物品.txt`,`交易物品.txt` | 物品名清单 | 🟢 | ItemPolicy | 背包/交易 | - |
| 师门任务参数 | 单师门 config | 任务次数/捉宠/南海南诏传送/商会ID | 🟢 | SchoolTaskProfile | 单师门 | - |
| 采集映射表 | 生活王 pz.ini `编辑框1` | 1-10级=地图:矿/药 映射 | 🟢 | CollectMap | 挖矿/采药 | 解析多级映射字符串 |

---

## 4. 流程 / 状态机类（蓝本，新后端重写）

| 流程 | 主来源 | 可复用 | 接入 Controller | 风险 |
|---|---|---|---|---|
| 普通按键打怪 | uu | 🟡 | NormalHangController | 状态细节运行态确认 |
| 技能释放节奏 | uu + 生活王间隔 | 🟢 | InputService | - |
| 马贼流程 | uu(骨架)+马2.31(坐标)+队员马(跟随) | 🟡 | HorseBanditModeController | 三来源融合，见 ModuleComparison §3 |
| 自动跟随队长 | 队员马双开 | 🟢 | HorseBanditModeController | 句柄运行态 |
| 抢 NPC 流程 | uu 抢抢页 | 🔴 | NpcGrabController(Targeting+Action) | 运行态确认各目标 |
| 自动反贼 | uu | 🔴 | AutoRebelController | 运行态确认 |
| 自动和尚 | uu | 🔴 | AutoMonkController | 与反贼共用框架 |
| 自动三环/副本 | uu 副本设置 | 🔴 | DungeonController | 老三/新三/我去左桥/站位 |
| 跑商流程 | 跑商OL | 🔴 | (P1) | 待解包 |
| 挖矿/采药流程 | 生活王 | 🟡 | LifeController(P1) | pz.ini 参数齐 |
| 挖图流程 | 挖图1.91 | 🔴 | LifeController(P1) | 待运行态 |
| 师门流程 | 单师门 | 🟡 | TaskPipelineController(P1) | config 字段齐 |
| 帮贡流程 | 帮贡五合一 | 🔴 | TaskPipelineController(P1) | 待运行态 |

---

## 5. 题库类

| 项 | 来源 | 格式 | 可复用 | 目标 | 接入 | 风险 |
|---|---|---|---|---|---|---|
| 答题题库 | `授权答题\bmp\bmp.db` | 非标准 SQLite(加密 57KB) | 🔴 | 统一题库(JSON/SQLite) | 答题(P1) | **加密**：需先定 `xx.dll` 解密方式，禁止强解第三方内容；仅处理自有题库 |
| 答题接口线索 | 队员马 `[打码]答题串` | 字符串 | 🟡 | - | 答题 | 与第三方打码区分，仅取自有题库匹配逻辑 |

---

## 6. 提取执行清单（后续，不在本轮）

按优先级提取到 `data/imported/`：

```
data/imported/uu/aj.txt                ← C:\sys\aj.txt (复制)
data/imported/ocr/zk_马2.31.txt         ← 各模块字库
data/imported/ocr/001_单师门.txt
data/imported/ocr/数字_*.txt
data/imported/ocr/fuben_生活王.txt
data/imported/routes/自定义坐标_马2.31.ini
data/imported/routes/采药坐标_生活王/*
data/imported/legacy-config/config_*.ini  ← schema 蓝本(去[root]/账号)
data/imported/question-bank/bmp.db.enc     ← 待解密标记
data/imported/index.json
```

`index.json` 每条记录字段（产品文档 §10.3）：
```json
{ "sourceModule":"", "sourcePath":"", "dataType":"", "targetModule":"", "priority":"", "status":"", "notes":"" }
```

---

## 7. 全局风险登记

| 风险 | 影响 | 缓解 |
|---|---|---|
| 坐标系未知（小地图 vs 客户区像素） | 返点/巡逻错位 | 接入前用 1 张图实测校准，建立换算 |
| bmp.db 加密 | 题库不可用 | 单列解密调研；不强解；仅自有数据 |
| uu 行为靠运行态推断 | 状态机不准 | P0 先做可静态验证部分；运行态逐项补全，UI 标接入状态 |
| 句柄/地址类字段(地址1=...) | 不可移植/版本相关 | **不复用内存地址**；句柄运行态获取，地址等 64 位再议 |
| bmp 模板海量未分类 | 识别库杂乱 | 建模板归类流程，按用途打标 |
| 明文账号(账号.txt) | 隐私/安全 | 不入库，见 AuthRemovalPlan |

> 本计划不修改任何原始文件。
