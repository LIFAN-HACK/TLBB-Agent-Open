# 视觉插槽 V2 接入记录

日期：2026-07-11  
状态：后端已接入，离线夹具通过，待真实游戏窗口回归

## 1. 生产链

```text
客户区截图
  -> Core L1 V2 七类 ONNX
  -> 对应 L1 裁剪
  -> 常驻 Vision V2 Python 宿主
  -> L2 人物/宠物/技能/小地图/固定控件
  -> PerceptionHub
  -> RuntimePerceptionSnapshot
  -> Controller / Action Planner
```

旧点阵字库和旧固定区域 OCR 不参与生产回退。

## 2. 已接入能力

| 能力 | 运行实现 | 失败语义 |
|---|---|---|
| 目标/人物/宠物/技能/小地图布局 | R6 七类 ONNX | 缺框即未知，不用旧区域补值 |
| 人物 HP/MP | `l2_slot_worker.py --mode player` | 任一比例未知时不触发对应恢复动作 |
| 宠物状态/HP | `--mode pet` | 面板连续缺失两次才判休息或未出战 |
| 底部技能 CD | `--mode hotkeys` | 只支持七个可见槽；未知不释放 |
| 血迹/共生 | `fixed_ui_slot_worker.py` | 身份或 CD 任一未知均不点击 |
| 小地图怪物 | `--mode minimap` + `MinimapTemporalFusionService` | 单帧只作观察；时序融合后才输出运行门禁三态，资源点永不计入怪物 |
| 小地图坐标 | `minimap_coordinate_worker.py` | 模板与结构不一致时不输出坐标 |
| 固定面板 | L1-Aux 多尺度模板 | 低置信度不进入 L2 动作 |
| 面板控件 | `ui_panel_slot_worker.py` | 非白名单动作或未确认控件不暴露点击点 |
| 动作规划 | `slot_action_planner.py` | 只有 `action=click` 可交给执行器 |

## 3. 运行约束

1. 所有坐标都是客户区坐标，不叠加桌面窗口偏移。
2. L2 只读取 L1 裁剪，不直接分析整张游戏画面。
3. 人物、宠物、技能和小地图分别限频，避免每个截图帧重复推理；小地图缓存结果按采样序号去重。
4. `VisionActionExecutor` 只接受规划器确认的 `Click`，并再次校验客户区边界。
5. 商店未知物品固定为保护，不允许出售。
6. 清包实际出售仍未开放，继续保持候选优先和人工确认。

## 4. 战斗链变化

- `VisionCombatResultMapper` 现在把 V2 人物 HP、MP、宠物 HP 和宠物出战状态送入挂机控制器。
- 技能 CD 服务改为读取 V2 底部七槽，不再读取用户截取的旧图色模板。
- 血迹和共生优先使用 V2 固定技能槽识别与规划点击，不要求玩家填写快捷键或固定坐标。
- 小地图单帧 `Absent/Unknown` 不停止轮询。最近 1200ms 内有任一阳性帧则保持 `Present`；只有至少 5 个连续无怪采样、跨度至少 800ms，并且连续 3 次找怪仍无目标面板，才确认 `Absent`。目标面板立即覆盖无点结果；抢怪模式不受该暂停门禁影响。
- 地图坐标和死亡弹窗识别已改走 V2 链。

## 5. 离线验证

命令：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run `
  --project 'tools\TlbbAgent.VisionHarness\TlbbAgent.VisionHarness.csproj' `
  -c Release --no-build -- `
  --v2-only `
  --vision-root '<TLBB_VISION_ROOT>'
```

2026-07-11 实测：

- R6 ONNX SHA256 校验通过。
- 样本 `pet_000019.png`：人物 HP=98%、MP=98%、宠物=Deployed/27%。
- 技能槽=7，宠物恢复技能候选=2。
- 单帧夹具输出：小地图怪物=Absent/0，坐标=1,80；动作规划对此单帧结果输出 `Noop`，不会直接停止轮询。
- L1 总耗时 p50=143.3ms，L1 ORT p50=116.3ms（同机多次运行会受 CPU 负载影响）。
- L2 核心槽总耗时 p50=91.7ms。
- 材料面板确认动作输出 Click；非法动作输出 Noop。
- 未列入策略的商店物品输出 KeepItem。
- 总结果：`VISION_HARNESS PASS`。
- 小地图专项：闪烁 2 帧有圈+3 帧无圈仍为 `Present`；单帧无点为 `Unknown`；5 帧/800ms 无点+3 次找怪失败才为 `Absent`；目标面板覆盖无点；资源点隔离；缓存帧重复播放不累计。

## 6. 待真实窗口验收

1. 人物血蓝在不同血量阶段的连续稳定性。
2. 宠物休息、出战、死亡、再次出战的两帧状态切换。
3. 七个底部技能槽的 Ready/Cooling 与游戏动画同步情况。
4. 血迹/共生真实点击及点击后的状态复核。
5. 小地图 Present/Absent 在有怪、无怪和地图切换时的三态稳定性；当前尚无独立世界场景怪物检测器，未选中目标时仍以小地图时序和找怪失败次数为准。
6. 死亡框、自动寻路、商店和背包在真实窗口上的定位。
