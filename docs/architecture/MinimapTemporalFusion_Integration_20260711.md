# 小地图标记时序融合接入记录

日期：2026-07-11  
状态：代码与离线回归完成，待真实窗口连续回归

## 目标

修正两个错误假设：

1. 黄色/橙色图形不一定是怪物，黄色菱形资源点必须独立输出。
2. 单帧没有标记不等于附近无怪，UU 圈动画和边缘遮挡都会造成短暂缺失。

## 生产数据流

```text
LatestFrameService
  -> L1 minimap 裁剪
  -> l2_slot_worker.py --mode minimap（200ms）
  -> 单帧 MinimapPerception
  -> MinimapTemporalFusionService（按窗口隔离）
  -> RuntimePerceptionSnapshot.Minimap
  -> NormalHangController 小地图门禁
```

视觉侧 `minimap_temporal_fusion.py` 是规则基准；生产端使用等价 C# 服务，以便多窗口隔离、直接接收选怪反馈且不增加进程间往返。

## 单帧契约

- `frame_marker_state`：本次 L2 的 `Present/Absent/Unknown`。
- `nearby_monster_count` / `monster_markers`：怪物候选。
- `uu_target_ring_count`：UU 圈样式候选。
- `native_or_uu_orange_dot_count`：原生橙点或 UU 实心阶段候选。
- `resource_marker_count` / `resource_markers`：资源点，仅供采集/地图能力使用。
- `ObservationSequence`：实际执行 L2 时对应的截图序号，用于阻止缓存结果被重复累计。

## 融合规则

1. 时间窗为 1200ms。
2. 窗口内任一怪物阳性帧、目标面板存在或未来的世界怪物检测阳性，输出 `Present`。
3. 单帧 `Absent/Unknown` 输出 `Unknown`，不停止基础打怪。
4. 仅当窗口内所有有效采样均为 `Absent`、采样数至少 5、跨度至少 800ms，并且成功发送选怪键后连续 3 次仍未出现目标面板，输出确认 `Absent`。
5. 目标面板出现立即清零找怪失败计数并覆盖小地图无点。
6. 资源点数量不参与任何怪物阳性判定。
7. 同一个 `ObservationSequence` 无论被读取多少次，只累计一次。

## 挂机行为

- 打死换怪、BOSS、副本等非抢怪模式：只有时序确认 `Absent` 且当前无目标、历史也未锁定目标时，暂停无效选怪。
- 抢怪模式：不使用该暂停门禁，继续按自身快速轮询逻辑运行。
- 小地图未知或单帧无点：不做独立决策，继续由目标面板、血量和战斗状态机决定。

## 已验证用例

- 2 帧有圈 + 3 帧无圈：保持 `Present`。
- 单帧无点：`Unknown`。
- 5 个连续无点采样、跨度 800ms、3 次选怪失败：确认 `Absent`。
- 目标面板可见：立即 `Present`，失败计数清零。
- 仅有 3 个资源点：不变成怪物阳性。
- 同一缓存无点帧重放 6 次/1 秒：仍只有 1 个有效样本，不确认无怪。

## 已知限制

当前没有已发布的世界场景怪物检测器，因此“画面中有未选中的怪物”尚不能直接作为阳性证据。现阶段通过小地图时序、目标面板覆盖和三次找怪失败门槛避免单帧误停；待世界怪物模型可用后，可直接传入 `worldMonsterVisible`，无需改融合规则。
