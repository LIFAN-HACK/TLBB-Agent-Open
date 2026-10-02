# 天山技能资产表

- 技能数：25
- 官网图标完整：25/25
- 游戏内已校准：0/25
- 安全边界：`official_seed_only` 只允许输出候选身份，不得自动释放。

| ID | 技能 | 类型 | 目标 | 释放规则 | 风险标签 | 运行资格 |
|---|---|---|---|---|---|---|
| ts_001 | 雁南飞 | direct_damage | enemy | allow_after_calibration_and_user_enable | - | official_seed_only |
| ts_002 | 鹰击长空 | aoe_damage | self_aoe | allow_after_calibration_and_user_enable | multi_target | official_seed_only |
| ts_003 | 冰肌玉骨 | resistance_buff | friendly_or_self | manual_only | friendly_target | official_seed_only |
| ts_004 | 凤舞九天 | direct_damage | enemy | allow_after_calibration_and_user_enable | - | official_seed_only |
| ts_005 | 白驹过隙 | movement_buff | self | manual_only | movement | official_seed_only |
| ts_006 | 铁锁横江 | control | enemy | control_opt_in_after_calibration | control | official_seed_only |
| ts_007 | 十面埋伏 | group_stealth | friendly_or_self | manual_only | friendly_target,multi_target,stealth | official_seed_only |
| ts_008 | 金蝉脱壳 | self_stealth | self | manual_only | stealth | official_seed_only |
| ts_009 | 移花接木 | combat_buff | self | buff_opt_in_after_calibration | multi_target_effect | official_seed_only |
| ts_010 | 兰花点穴手 | control | enemy | control_opt_in_after_calibration | conditional_cast,control,requires_stealth | official_seed_only |
| ts_011 | 柳暗花明 | friendly_stealth | friendly | manual_only | friendly_target,stealth | official_seed_only |
| ts_012 | 凝霜甲 | passive_defense | none | never_cast | passive | official_seed_only |
| ts_013 | 雪花六出 | direct_damage | enemy | allow_after_calibration_and_user_enable | - | official_seed_only |
| ts_014 | 融雪式 | defensive_stance | self | stance_opt_in_after_calibration | stance | official_seed_only |
| ts_015 | 踏雪无痕 | ground_movement_stealth | ground | manual_only | conditional_cast,ground_target,movement,requires_stance,stealth | official_seed_only |
| ts_016 | 疏影扇 | passive_weapon | none | never_cast | passive | official_seed_only |
| ts_017 | 梅花三弄 | damage_with_slow | enemy | control_opt_in_after_calibration | control,slow | official_seed_only |
| ts_018 | 折梅式 | offensive_stance | self | stance_opt_in_after_calibration | stance | official_seed_only |
| ts_019 | 寒梅怒放 | direct_damage | enemy | allow_after_calibration_and_user_enable | requires_stance | official_seed_only |
| ts_020 | 阳歌天钧 | direct_damage | enemy | allow_after_calibration_and_user_enable | requires_half_rage | official_seed_only |
| ts_021 | 阳春白雪 | cleanse_immunity | self | condition_engine_required | cleanse,immunity,requires_debuff_detection | official_seed_only |
| ts_022 | 阳关三叠 | critical_buff | self | buff_opt_in_after_calibration | - | official_seed_only |
| ts_023 | 天地明灭 | position_swap | player | manual_only | control,movement,position_swap,pvp_sensitive | official_seed_only |
| ts_024 | 同生共死 | control_health_cost | enemy | manual_only | control,health_cost | official_seed_only |
| ts_025 | 冰凝霜华 | control | enemy | control_opt_in_after_calibration | conditional_cast,control,requires_stealth | official_seed_only |
