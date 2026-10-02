# 逍遥技能资产与识别基准 P0

## 结论

逍遥技能资产已经形成可复用的客户端版本级共享库。当前资产链路为：

```text
L1 skill_bar crop
  -> L2 3行x14列切槽
  -> icons_ingame 身份匹配
  -> 独立 CD 判断
  -> 输出 SkillId / SlotId / Key / Cooldown / Score
```

本对话只维护训练与资产，不继续修改后端运行链。`icons_raw/` 和
`seed_index/` 是离线候选资产，当前运行时不加载；后端接入要求见
`docs/requirements/SkillAsset_DynamicSlot_Backend_Handoff_20260713.md`。

官网图标只用于建立技能名称、说明和共享游戏渲染库候选。共享库按客户端版本集中
建设一次，不按玩家、账号或技能摆放重复校准。只有经过游戏内截图复核后进入
`icons_ingame/` 的模板，才具备成为 `Matched` 的资产资格。

## 当前资产

- 技能目录：`data/skill_library/catalog.jsonl`
- 官网种子：`data/skill_library/icons_raw/`
- 游戏内模板：`data/skill_library/icons_ingame/`
- 运行模板索引：`data/skill_library/match_index/index.json`
- 离线候选索引：`data/skill_library/seed_index/index.json`
- 人工复核覆盖：`data/skill_library/calibration_overrides.json`
- 抓取工具：`tools/skill_library/scrape_official_skills.py`
- 校准工具：`tools/skill_library/calibrate_from_ingame.py`
- 运行索引工具：`tools/skill_library/build_match_index.py`
- 候选索引工具：`tools/skill_library/build_seed_index.py`
- 匹配基准：`tools/skill_library/match_slot_icon.py`

## 槽位输出目标

后端应从每个当前可见槽位读取：

```text
Surface / SlotId / Row / Column / Key
SlotBox / ClickX / ClickY
SkillId / SkillName / Identity
MatchScore / MatchMargin
Cooldown / HangUsable / Castable
```

身份、CD、策略和动作位置必须分开处理。技能换槽后，`SkillId` 不变，键位或点击点
必须来自 L2 当帧槽位，不得继续使用历史固定键位。

## 当前基准

- 官方逍遥记录：24，缺图 0。
- 当前共享游戏内模板总量：159，覆盖 19 个技能 ID。
- 逍遥早期基准：102 张模板，覆盖 14 个已校准技能。
- matched 留出：148，Top1 100%，达阈值且正确 100%。
- 其他职业/空槽负样本：107，错误 matched 0。
- 真实首帧：42 槽，20 matched，10 empty。
- 缩放回归：2560x1369、1920x1027、1280x685 均通过关键槽身份核对。

## 生成顺序

```powershell
$py = '<PYTHON_EXE>'

& $py tools\skill_library\scrape_official_skills.py `
  --sources src\TlbbAgent.Core\models\vision\v2\l2_slots\public_skill_sources.example.json `
  --output-root data\skill_library --sect 逍遥

& $py tools\skill_library\calibrate_from_ingame.py `
  --input '<逍遥截图目录>' --library-root data\skill_library `
  --sect 逍遥 --bbox '<仅离线全屏样本需要填写的 skill_bar 框>'

& $py tools\skill_library\build_match_index.py `
  --library-root data\skill_library --high-threshold 0.79 `
  --low-threshold 0.62 --margin 0.06

& $py tools\skill_library\match_slot_icon.py `
  --library-root data\skill_library `
  --bench data\skill_library\reports\bench_manifest.json `
  --report data\skill_library\reports\match_bench.md
```

`--bbox` 仅用于离线校准原始全屏截图。正式运行时必须使用 L1 框或经明确声明的
安全降级 ROI，不得把 2560 分辨率绝对坐标写死到运行逻辑。

## 后端待办边界

1. 当前运行时仍是 `icons_ingame_only`，官网候选没有接入运行时。
2. 当前 L1 V4 只有统一 `skill_bar` 类，没有独立 `right_skill_bar`。
3. 动态摆放、全局 SkillId 策略、候选状态和右侧技能栏接入均由后端对话完成。
4. 本资产对话继续负责新门派模板、负样本、阈值基准和模型重训。
