# TLBB L2 Slots

本目录是二级插槽能力的首版交付，输入必须来自 L1 框内 crop，或由调用方传入全屏图加 L1 bbox 后再裁剪。

## 已覆盖

- `player`: 玩家状态框，输出 `hp_ratio`、`mp_ratio`。蓝量低于策略阈值时，调用方再结合技能插槽的 `cd_state=ready` 决定是否释放血祭。
- `pet`: 宠物状态框，输出 `state=deployed|unknown`、`hp_ratio`。如果 L1 没有 `pet_status_panel`，调用方按 `resting_or_absent` 处理。
- `hotkeys`: 技能栏底部快捷键 F1-F7，输出每个槽位的 `cd_state` 和 crop 内点击点；不识别职业技能图标身份。
- `minimap`: 小地图框，输出 `nearby_monster_present`、`nearby_monster_count`、怪物点 crop 坐标、蓝点数量，以及 `coord_text_roi_normalized` 给 OpenText/数字 OCR 读取坐标。
- `material`: 面板/材料加工基础检测，输出暗色面板分数、金色按钮候选、进度条比例。当前只作为候选插槽，不把按钮语义写死。
- `monthly_card`: 月卡领取候选插槽，输出面板/按钮候选和需要 OpenText 确认的关键词。
- `town_return`: 回城/传送候选插槽，动作后必须用地图名或坐标 OCR 复核。
- `shop_cleanup`: 商店/NPC 清包候选插槽，只能配合外部保留/出售策略使用。
- `auto_nav`: 自动寻路候选插槽，当前没有专用寻路图片训练，只输出 OCR/策略确认入口。

## 命令示例

```powershell
python train/artifacts/l2_slots/l2_slot_worker.py --mode player --image frame.png --bbox 0,0,240,66
python train/artifacts/l2_slots/l2_slot_worker.py --mode pet --image frame.png --bbox 107,77,239,138
python train/artifacts/l2_slots/l2_slot_worker.py --mode hotkeys --image frame.png --bbox skill_bar_x1,skill_bar_y1,skill_bar_x2,skill_bar_y2
python train/artifacts/l2_slots/l2_slot_worker.py --mode minimap --image frame.png --bbox 2320,0,2560,228
python train/artifacts/l2_slots/l2_slot_worker.py --mode material --image frame.png --bbox x1,y1,x2,y2
python train/artifacts/l2_slots/l2_slot_worker.py --mode monthly_card --image frame.png --bbox x1,y1,x2,y2
python train/artifacts/l2_slots/l2_slot_worker.py --mode town_return --image frame.png --bbox x1,y1,x2,y2
python train/artifacts/l2_slots/l2_slot_worker.py --mode shop_cleanup --image frame.png --bbox x1,y1,x2,y2
python train/artifacts/l2_slots/l2_slot_worker.py --mode auto_nav --image frame.png --bbox x1,y1,x2,y2
```

## 坐标约定

- `click_point` 或 marker 坐标默认是 crop 内坐标。
- 接入侧换算全屏坐标：`screen_x = l1_bbox.x1 + crop_x`，`screen_y = l1_bbox.y1 + crop_y`。
- 如果调用时传了 `--bbox`，worker 会自动补充 `screen_click_point`、`screen_center`、`screen_bbox`、`screen_slot_bbox` 等全屏坐标字段。
- 小地图和材料面板如果需要读字，优先把返回的 ROI 再交给 `IOpenTextRecognizer`，不要为地图名、材料名、按钮文字做封闭分类。

## OCR 确认

策略槽支持传入 OpenText 结果：

```powershell
python train/artifacts/l2_slots/l2_slot_worker.py --mode monthly_card --image frame.png --bbox x1,y1,x2,y2 --ocr-text "月卡 奖励 领取"
```

只有同时满足 OCR 关键词和按钮候选时，`policy_state` 才会变成 `confirmed_candidate`。

如果整张全屏或大范围 crop 里候选按钮过多，worker 会返回：

- `button_candidate_ambiguity=high`
- `button_candidate_usable=false`
- `primary_button_candidate=null`

这种情况不允许点击，调用方需要传更紧的面板 bbox 或配置固定 ROI。

## 点击决策

不要让业务脚本直接消费 L2 原始候选点执行点击。统一走：

```powershell
python train/artifacts/l2_slots/slot_action_planner.py player_mana --player player.json --pet-restore pet_restore.json
python train/artifacts/l2_slots/slot_action_planner.py hotkey --hotkeys hotkeys.json --key F1
python train/artifacts/l2_slots/slot_action_planner.py policy --slot-result monthly_card.json --policy-mode monthly_card
python train/artifacts/l2_slots/slot_action_planner.py minimap_tracking --minimap minimap.json
python train/artifacts/l2_slots/slot_action_planner.py auto_nav_arrival --coord-text "100,204" --target-x 100 --target-y 204
python train/artifacts/l2_slots/slot_action_planner.py shop_item_policy --item-ocr-text "破损的粗布" --keep-items "绑定,宝石" --sell-items "破损,粗布"
```

只有返回 `action=click` 的结果才允许执行点击；返回 `noop` 必须跳过并记录 `reason`。

自动寻路当前只做坐标 OCR 后的到达判断，返回 `arrived / continue_navigation`。清包当前只做物品 OCR 后的保留/允许出售判断，未知物品永远返回 `keep_item`。

## 当前限制

- 小地图点数偏向“宁可多报”，用于判断附近是否有怪足够；精确轨迹/种类需要继续采样后细化颜色过滤。
- 宠物休息状态目前由 “L1 无宠物状态框” 表达为 `resting_or_absent`；若要区分休息按钮/召回状态，需要补采宠物未出战但界面可见的样本。
- 材料加工缺少明确正样本和按钮语义标注，现阶段只能给候选面板/候选按钮，不能直接承诺自动点击正确加工按钮。
- 月卡、回城、NPC 清包当前也是弱策略插槽，不是可靠训练类。必须走 `policy_slots.example.json` 的关键词、按钮候选和安全策略。
- `datasets/07_寻路模块` 当前无图片，自动寻路先依赖小地图坐标 OCR 和脚本侧路线策略。
