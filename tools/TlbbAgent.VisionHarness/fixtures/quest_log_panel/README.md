# quest_log_panel fixtures

原先的实机截图包含角色名与其他玩家聊天内容，已从公开仓库移除。
如需运行 Long Batian NPC 对话 hard-negative 回归，请自备一张 1920x1080 的 NPC 对话画面，
放到本目录并命名为 `long_batian_npc_dialog_1920x1080.png`，或通过环境变量
`TLBB_QUEST_LOG_HARD_NEGATIVE_FIXTURE` 指定路径。缺失时该项会被跳过（SKIP），不计为失败。

The original in-game screenshot contained player names and chat and was removed from the public
repository. Supply your own 1920x1080 NPC-dialog frame here (same file name) or point
`TLBB_QUEST_LOG_HARD_NEGATIVE_FIXTURE` at it. When missing, the check is skipped.
