# 技能资产、动态槽位与策略层后端交接

> 2026-07-13 更新：独立 `right_skill_bar` L1 双档模型已交付。右侧技能栏部分以 `RightSkillBar_L1_DualProfile_Backend_Handoff_20260713.md` 为最新接入规范；未完成该接入前仍保持 `unknown/noop`。

- 日期：2026-07-13
- 资产生产：`<TLBB_VISION_ROOT>`
- 后端工程：`<REPO_ROOT>`
- 职责边界：当前对话只交付模型、清洗数据、资产库、评测和契约；运行接入由后端对话完成。

## 给后端对话的直接指令

```text
请在 <REPO_ROOT> 接入技能资产动态槽位链路。不要要求每个玩家或每种技能摆放
重新校准。L1 只负责定位当前可见技能区域；L2 在每个区域内逐槽输出稳定 SkillId、
当前 Key/ClickPoint、CD、置信度和身份状态；状态机按 SkillId 查策略并使用当帧槽位动作。
icons_ingame 的 Matched 才能进入动作门禁；icons_raw/seed_index 的 Candidate 只用于诊断，
永不释放。主 L1 V4 没有独立 right_skill_bar，但资产对话已交付独立双档模型；请按
RightSkillBar_L1_DualProfile_Backend_Handoff_20260713.md 接入。接入完成前右侧两列仍保持
unknown/noop。请实现下述契约、门禁与验收测试，不改写资产生成脚本和目录。
```

## 当前可交付资产

### L1 V4

- 目录：`<TLBB_VISION_ROOT>\train\artifacts\l1_layout_v4_20260712_target_pet_geometry_release`
- ONNX：`layout_v4_7class.onnx`
- SHA256：`903e52f163d2c3467bdb1ec2ea2f8b83df07937e813c0c67d62d4f7305d90a43`
- 训练集：936 张，`681/160/95`
- ONNX test：Precision 0.99796、Recall 0.99850、mAP50 0.99350、mAP50-95 0.99261。
- 类别：`target_panel / target_hp_bar / target_name / player_status_panel / pet_status_panel / skill_bar / minimap`。

限制：主 L1 V4 仍只有统一 `skill_bar`，没有独立 `bottom_skill_bar / right_skill_bar / pet_skill_bar`。
右侧两列现由独立双档 L1 产物提供，不能修改主模型类别表或用固定坐标冒充接入；详见最新右侧技能栏交接任务书。

### 技能资产

- 根目录：`<REPO_ROOT>\data\skill_library`
- `catalog.jsonl`：252 条，其中十大门派官网记录 247 条。
- `policy_overrides.json`：技能业务语义与默认门禁。
- `icons_ingame/`：159 张游戏内模板，覆盖 19 个 SkillId。
- `match_index/index.json`：当前可信运行索引，`runtime_source_policy=icons_ingame_only`。
- `icons_raw/`：官网原图资产。
- `seed_index/index.json`：49 条逍遥/天山离线候选，`diagnostics_only_never_cast`。
- 官方候选盲测：117 查询，Top-1 83.76%，严格确认 72，确认误匹配 0，覆盖 61.54%。
- 十门派人工复核包：`<TLBB_VISION_ROOT>\train\review\skill_assets_official_10sects_20260713`。

## 当前代码边界

本次交接前已撤回资产对话写入的动态摆放和官网候选运行接入。当前状态应理解为：

1. `skill_icon_runtime.py` 只加载 `icons_ingame/ + match_index/`。
2. C# 身份枚举没有 `Candidate`。
3. 构建不会打包 `icons_raw/` 和 `seed_index/`。
4. 现有 `SkillCooldownRecognitionService` 仍按配置热键先找底栏槽位。
5. 现有 `NormalHangController` 仍有热键与 SkillId 绑定逻辑，不满足换槽无关目标。

这些是后端待改项，不是资产缺失。

### 当前回归证据

- `dotnet build TlbbAgent.sln -c Release --no-restore`：通过，0 错误，13 个既有 obsolete 警告。
- `TlbbAgent.VisionHarness`：PASS。
- `TlbbAgent.CombatHarness`：仅 1 项失败，名称为 `visual-skill-config-bound-loop`。
- 失败现象：未配置 `Skills[]` 时，现有控制器仍从视觉库存发送了 W；输出为
  `no-config=Q,W,Q,Q`。
- 根因：现有 `VisualSkillBindingAllows` 在没有显式绑定时返回 true，与 harness 的
  “未配置不得自主释放”断言冲突。

该失败留给后端对话处理。资产对话不再修改控制器或 harness 以掩盖此问题。

## 目标链路

```text
全屏帧
  -> L1 检测一个或多个技能区域
  -> 每个 L1 crop 归一化并切槽
  -> L2 身份匹配 + 独立 CD 检测
  -> 汇总当前帧 SkillInventory
  -> SkillId 策略、条件、优先级、公共CD、去抖
  -> 使用当前槽位 Key 或 ClickPoint 执行动作
  -> 下一帧复核
```

不得使用 `配置热键 -> 固定槽位 -> 识别技能` 作为主链。正确方向是
`当前槽位 -> 识别 SkillId -> 查全局策略 -> 使用当前槽位动作`。

## L2 输出契约

每个槽位至少输出：

```json
{
  "surface": "bottom_skill_bar",
  "slot_id": "r3c2",
  "row": 2,
  "column": 1,
  "skill_id": "xy_004",
  "skill_name": "弹指神功",
  "identity_state": "matched",
  "identity_source": "icons_ingame",
  "score": 0.93,
  "margin": 0.12,
  "key_hint": "E",
  "activation_kind": "keyboard",
  "click_point": [1130, 1048],
  "cd_state": "ready",
  "hang_usable": true,
  "policy_class": "damage",
  "release_rule": "allowed_by_policy"
}
```

必须保留 `matched / candidate / unknown / empty` 和 `ready / cooling / unknown`，不能把低置信
结果硬判成 ready 或可释放。

## 动作门禁

只有以下条件全部成立才允许动作：

1. `identity_state == matched`。
2. 来源是 `icons_ingame`，不是官网候选。
3. `cd_state == ready`。
4. `hang_usable == true`。
5. SkillId 在全局允许策略内，且条件、优先级、公共 CD 均通过。
6. 当前槽位有合法 `key_hint` 或经窗口边界校验的 `click_point`。

其他情况统一 `noop`。技能策略按 SkillId 保存，不按槽位或热键保存。重复 SkillId 同时
出现时，按策略优先级和当前 ready 状态选一个；去抖键使用 SkillId，不使用旧槽位编号。

## 多区域要求

后端数据结构必须支持同帧多个技能 surface：

- `bottom_skill_bar`：键盘释放。
- `right_skill_bar`：键盘或鼠标，取决于当帧槽位是否有键位。
- `pet_skill_bar`：通常鼠标点击；血祭可在第 1 或第 2 宠物技能槽。
- `buff_bar`：只读状态，不执行技能点击。

主 L1 V4 只可靠提供统一 `skill_bar`；独立 `right_skill_bar` 双档模型已经交付，按专项任务书接入。
独立 `buff_bar` 仍须保持 unavailable，不允许用全屏模板或 2560 绝对坐标冒充 L1。

## 验收用例

1. 配置允许 `xy_004`，历史热键 W，L2 当帧识别在 E：发送 E，不发送 W。
2. 同一技能换到底栏其他槽位：无需重新校准，仍按 SkillId 生效。
3. SkillId 不一致：即使图标分数高、CD ready，也不动作。
4. `Candidate`：即使 score 0.99，也必须 `HangUsable=false / Castable=false / noop`。
5. `Unknown` 或 CD `cooling/unknown`：不动作。
6. 两个同 SkillId 槽位：只选择一个 ready 且可动作的槽位，SkillId 去抖生效。
7. 800x600、1280x720、1920x1080、2560x1369 和带标题栏窗口：坐标均来自当前 crop。
8. 仅右侧技能栏可见，但专项模型缺失、哈希错误或无合法候选：返回 unavailable/noop，不点击。
9. 宠物血祭位于第 2 槽：按识别结果点击第 2 槽，不写死第 1 槽。
10. 资产版本或哈希不匹配：拒绝加载，不降级到官网候选自动释放。

## 版本与日志

运行快照至少记录：

```text
client_version
ui_resource_version
catalog_version
match_index_sha256
l1_model_sha256
surface / slot_id / skill_id / identity / cd / score / margin
decision / blocked_reason / dispatched_key_or_point
```

## 禁止事项

- 不做每玩家校准。
- 不按用户技能摆放生成独立模板。
- 不把官网图直接设为 Matched。
- 不把技能策略绑定到固定热键或固定槽位。
- 不在 L1 缺失时用全屏模板授权点击。
- 不把 CD 识别与技能身份识别混成一个硬分类。
