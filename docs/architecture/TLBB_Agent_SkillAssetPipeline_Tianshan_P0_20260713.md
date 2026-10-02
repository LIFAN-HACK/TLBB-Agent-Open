# 技能资产驱动识别与天山 P0

## 结论

职业扩展采用统一资产链，不训练“每个门派一个技能分类模型”：

```text
官网技能名/说明/图标
  -> 离线候选索引
  -> 游戏内 ready/cooling 截图复核
  -> icons_ingame 共享模板
  -> L2 身份与 CD 输出
  -> 后端策略层决定是否释放
```

官网图标不能保证与当前客户端槽位像素完全一致，只能辅助认名和集中生产模板，
不能直接获得输入权限。资产按 `客户端版本 + UI资源版本` 共享，不按玩家或技能摆放
重复校准。

## 资产状态语义

| 状态 | 来源 | 资产侧含义 |
|---|---|---|
| `Matched` | `icons_ingame/` | 已通过游戏内截图复核，可交给后端继续做策略和 CD 门禁 |
| `Candidate` | `icons_raw/` + `seed_index/` | 离线诊断候选，永不直接授权释放 |
| `Unknown` | 分数或间隔不足 | 保持未知 |
| `Empty` | 空槽 | 空槽 |

`Candidate` 当前只存在于资产索引语义中，尚未接入 C# 运行契约。后端若接入，必须
保证 `Candidate` 不能成为 `HangUsable` 或 `Castable`。

## 当前覆盖

- 总目录：252 条。
- 官网十大门派资产：247 条，缺图 0。
- 普通技能：4；道具样本：1。
- 游戏内模板：159 张，覆盖 19 个已校准技能 ID。
- 天山官网页：25 条、25 图标、缺图 0、失败请求 0。
- 天山游戏内模板：0；当前全部为 `official_seed_only`。
- 离线官网候选索引：逍遥与天山共 49 条；其他门派尚未人工复核，不进入候选索引。

天山资产位于：

- `data/skill_library/icons_raw/ts_001..ts_025`
- `data/skill_library/reports/tianshan/asset_table.csv`
- `data/skill_library/reports/tianshan/asset_table.json`
- `data/skill_library/reports/tianshan/review_sheet.png`
- `data/skill_library/reports/tianshan/client_version_bootstrap_manifest.json`

## 天山策略

`data/skill_library/policy_overrides.json` 已对 25 个技能做人工语义分类：

- 直接伤害/AOE：完成游戏内模板生产并显式允许后，才可进入后端释放候选。
- 控制：必须显式开启控制策略。
- 战斗 Buff/姿态：需要独立防重复和 CD 门禁。
- 解控免疫：需要 Debuff 状态证据。
- 位移、地面点选、隐身、友方技能、PVP 换位、损血技能：默认仅手动。
- 被动技能：永不释放。

当前全部天山记录均为 `hang_usable=false`。

## 官网候选盲测

逍遥与天山 49 个官网图标对 117 个已标注逍遥游戏槽位测试：

- Top-1：83.76%。
- 候选阈值：score >= 0.70、margin >= 0.04。
- 达候选阈值且正确：72。
- 高阈值错误候选：0。
- 安全候选覆盖：61.54%。

该结果只证明候选层可用于辅助认名，不证明天山可以直接自动释放。官网图与游戏槽位
仍存在边框、缩放、快捷键字母和 CD 覆盖差异。

## 后端接入目标

动态摆放由后端按以下契约实现，本资产对话不修改运行代码：

1. L1 定位当前可见技能区域。
2. L2 逐槽输出 `SkillId / SlotId / Key或ClickPoint / Cooldown`。
3. 状态机用稳定 `SkillId` 查询允许列表、条件和优先级。
4. 真正动作来自 L2 当帧槽位，不来自旧配置中的固定位置。
5. 身份不是 `Matched`、CD 不是 `Ready`、策略不允许时一律不动作。

当前 L1 V4 没有独立 `right_skill_bar` 类，右侧两列技能栏不能宣称已覆盖。后端必须
在该模型产物补齐前保持 `unknown/noop`，不得用未经验证的固定坐标点击。

## 共享库生产流程

小号只是一次性资产生产数据源，不是最终用户配置步骤：

1. 打开天山技能栏，保持目标技能 ready，连续采集至少 2 帧。
2. 实际释放后采集 cooling 灰罩/数字状态，连续至少 2 帧。
3. 再采集不同窗口尺寸和 HUD 缩放样本。
4. L2 输入优先来自 L1 crop；离线全屏图可显式提供 bbox。
5. 用 Tooltip 名称交叉复核后导入 `icons_ingame/`。
6. 重建运行索引，跑留出集、跨门派负样本和多分辨率回归。
7. 最后依据 `policy_overrides.json` 决定资产策略，不在资产工具里直接发键。

## 生成命令

```powershell
$py = '<PYTHON_EXE>'

& $py tools\skill_library\scrape_official_skills.py `
  --sources src\TlbbAgent.Core\models\vision\v2\l2_slots\public_skill_sources.example.json `
  --output-root data\skill_library --sect 天山

& $py tools\skill_library\build_seed_index.py `
  --library-root data\skill_library --sect 逍遥 --sect 天山

& $py tools\skill_library\benchmark_seed_candidates.py `
  --library-root data\skill_library

& $py tools\skill_library\export_sect_assets.py `
  --library-root data\skill_library --sect 天山
```

收到天山游戏内截图后：

```powershell
& $py tools\skill_library\calibrate_from_ingame.py `
  --input '<天山截图目录>' --library-root data\skill_library `
  --sect 天山 --bbox '<仅离线全屏样本需要填写的 skill_bar 框>'
```

校准器只更新指定门派；没有有效模板时保留已有资产并返回失败。
