# Skill Library

本目录是客户端版本级共享技能身份资产库，分为两层：

- `icons_ingame/` + `match_index/index.json`：游戏内复核模板层，供当前 `icons_ingame_only` 运行匹配器使用。
- `icons_raw/` + `seed_index/index.json`：官网离线候选层，只用于认名、复核和集中生产模板，当前运行时不加载。

当前范围：十大门派官网资产表 + 逍遥运行校准 + 天山官网候选与人工策略。其余门派
尚未人工复核，不进入离线候选索引。生成、验证和后端交接说明见：

`docs/architecture/TLBB_Agent_Xiaoyao_SkillLibrary_P0.md`

`docs/architecture/TLBB_Agent_SkillAssetPipeline_Tianshan_P0_20260713.md`

`docs/requirements/SkillAsset_DynamicSlot_Backend_Handoff_20260713.md`
