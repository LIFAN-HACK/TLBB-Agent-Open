# 龙伴行目标模式：回点与安全清包

日期：2026-06-30

## 1. 本阶段目标

把“打怪自动回点、死亡自动回点、物品清包”做成可验收功能，而不是只做按钮入口。

优先级：

1. 外置按键打怪 + 自动回点闭环。
2. 死亡识别 + 死亡回点闭环。
3. 游戏内置挂机监控模式：程序负责开启、监控、回点，不接管打怪循环。
4. 安全清包：先识别和列候选，默认不出售；出售必须显式开启并受保护规则限制。

## 2. 回点目标模式

### 2.1 外置挂机回点

运行路径：

1. `NormalHangController` 负责外置打怪循环。
2. 每轮打怪前先调用 `IReturnPointRuntimeService.Evaluate(window)`。
3. 如果返回 `ShouldSuspendCombat=true`，本轮不发选怪、普攻、技能。
4. 回点子能力执行定点回点、土灵回点、死亡回点或卡点恢复。
5. 回到范围内后恢复打怪。

验收标准：

- 距离挂机点超过阈值时停止攻击并执行回点动作。
- 当前坐标回到到点距离内后恢复打怪。
- 回点动作连续失败达到次数后进入卡点恢复。
- 日志显示当前坐标、目标坐标、距离、执行动作、是否暂停战斗。

### 2.2 死亡自动回点

运行路径：

1. `DeathRecognitionService` 读取死亡提示区域。
2. 命中死亡关键词后，`ReturnPointRuntimeService` 优先执行死亡回点动作。
3. 死亡回点动作独立于普通回点动作，可配置热键、点击或热键后点击。

验收标准：

- 检测到死亡提示后不继续攻击。
- 执行死亡回点动作并写入日志。
- 死亡动作有独立冷却，避免循环狂点。

### 2.3 游戏内置挂机监控模式

定位：

程序不负责技能循环，不主动攻击怪物。程序只负责：

- 开启游戏内置挂机。
- 监控坐标、死亡、卡点、回点。
- 需要回点时暂停或关闭内置挂机，再执行回点。
- 回点完成后重新开启内置挂机。

开启方式候选：

- 小地图右侧自动战斗开关坐标。
- 技能面板/战斗页里的自动战斗入口。
- 用户校准后的热键或点击动作。

验收标准：

- UI 提供“打怪方式”：外置按键 / 游戏内置 / 自动判断。
- 游戏内置模式下不会发送技能循环键。
- 回点或死亡时能暂停/关闭内置挂机，回点完成后恢复。

当前状态：

- 外置按键打怪和回点链路已接通。
- 游戏内置挂机监控分支已接入：选择“游戏内置”后，`NormalHangController` 不发送选怪、普攻或技能，只运行回点/死亡/卡点监控。
- 游戏内置挂机的真实开关点击尚未接入，需要后续做最小安全控制：只点击用户校准的开关点，不做内存写入。

## 3. 清包目标模式

### 3.1 安全原则

默认只做“扫描与候选列表”，不卖、不丢、不存、不交易。

真正执行出售必须同时满足：

1. 用户开启“允许执行出售”。
2. 当前配置存在保留清单。
3. 候选物品不在保留清单、存放清单、交易清单。
4. 候选物品命中用户明确配置的出售关键词。
5. 日志输出每个候选的判定原因。

禁止：

- 未识别物品自动出售。
- 背包识别不完整时自动出售。
- 只凭格子颜色或图标相似度出售高价值物品。
- 未确认商店/NPC/随身商店状态时执行出售动作。

### 3.2 已落地的代码结构

新增契约：

- `InventoryGridConfig`：背包格子区域。
- `InventorySlotSnapshot`：单个格子的占用状态、坐标和可选物品名。
- `ItemPolicyConfig`：保留、存放、交易、出售关键词和未知物保护。
- `BagCleanupConfig`：清包模式、打开背包热键、网格配置和物品策略。
- `BagCleanupResult`：扫描结果、候选列表和是否实际执行。

新增服务：

- `InventoryRecognitionService`：从客户区截图识别背包网格里的空格/占用格。
- `ItemPolicyService`：读取旧物品清单，并判定保留/存放/交易/可卖/未知保护。
- `BagCleanupService`：执行清包分析。默认 `DryRun`，只生成候选，不点击出售。

接入：

- `UtilityController` 的清包步骤已经不再是裸热键。
- 如果 `UtilityConfig.Cleanup.Mode != Disabled`，清包步骤会先调用 `BagCleanupService`。
- 若未配置背包格子区域，会返回“需要校准”，不会盲扫。
- “生活/便捷”页面已提供安全清包配置入口：导入旧清单、配置背包网格、模拟分析、候选列表。
- 页面层只做配置和展示，实际识别/策略/执行仍由 `BagCleanupService`、`InventoryRecognitionService`、`ItemPolicyService` 处理。

### 3.3 旧数据复用

已支持导入：

- `马2.31\保留物品.txt` -> 保留清单。
- `单师门普通版 -1.37\存放物品.txt` -> 存放清单。
- `单师门普通版 -1.37\交易物品.txt` -> 交易清单。

当前不自动把任何旧清单当作“出售清单”。出售必须由用户显式配置关键词。

## 4. 验证结果

新增诊断：

```powershell
dotnet run --project .\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj -- bag-cleanup
```

已验证：

- 合成背包扫描：3 个占用格、3 个空格。
- 旧物品清单导入：127 条。
- 保留物品判定为 `Keep`。
- 命中出售关键词的物品判定为 `SellCandidate`。
- 未识别物品判定为 `BlockedUnknown`。
- `DryRun` 模式不会执行出售。
- UI 入口已接通，`ui-navigation` 诊断通过。
- `normal-hang` 诊断已覆盖游戏内置挂机监控：该模式日志正常，发送热键次数为 0。

回点诊断：

```powershell
dotnet run --project .\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj -- return-points
```

已验证：

- 导入地图 22 个、坐标 370 个。
- 普通回点、土灵回点、死亡回点、重试卡点、超时卡点均通过。

便捷动作兼容诊断：

```powershell
dotnet run --project .\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj -- utility
```

已验证：

- 清包/随身银行/随身商店/御龙帖旧动作序列仍可跑。
- 当清包服务禁用时，旧热键动作保持兼容。

## 5. 下一步

1. 做背包可视化校准：在当前游戏截图上框定第一格、列数、行数、格子大小，避免手填坐标。
2. 做物品 tooltip 采集：鼠标悬停格子，只读 OCR 物品名。
3. 做“随身商店/商店窗口状态确认”后，才允许执行出售。
4. 接入游戏内置挂机开关校准：只点击用户校准的开关点。
5. 做出售前二次确认和最近一次候选快照锁定，避免截图变化后误卖。

## 6. 2026-06-30 更新：背包自动校准与本轮验收

### 6.1 背包校准结论

当前客户端默认背包物品格实测不是 8 列，而是 10 列。通用网格评分容易误抓背包底部按钮区、联系人窗口或场景纹理，因此自动校准已改成两段式：

1. 优先识别默认背包区域里的连续金色长横线组。
2. 由横线组推导 10 列物品格：当前截图为 `10x6`，格子 `32x32`，间距 `3/3`。
3. 找不到结构线时才回退到通用网格评分。
4. 自动校准只用于默认背包位置；拖动过的背包仍建议用手动网格校准。

### 6.2 安全策略

- 当前清包仍默认 `DryRun`，只扫描、列候选，不出售。
- 未打开背包时，现场只读校准返回 `NOT_DETECTED`，不会误扫场景或右侧栏。
- 真实出售仍未开放为默认路径，后续必须先做商店窗口确认、候选快照锁定和二次确认。

### 6.3 最新验证命令

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build .\TlbbAgent.sln -v:minimal
& 'C:\Program Files\dotnet\dotnet.exe' run --project .\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj -- bag-cleanup
& 'C:\Program Files\dotnet\dotnet.exe' run --project .\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj -- normal-hang
& 'C:\Program Files\dotnet\dotnet.exe' run --project .\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj -- return-points
& 'C:\Program Files\dotnet\dotnet.exe' run --project .\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj -- utility
& 'C:\Program Files\dotnet\dotnet.exe' run --project .\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj -- ui-navigation
& 'C:\Program Files\dotnet\dotnet.exe' run --project .\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj -- live-bag-calibration
& 'C:\Program Files\dotnet\dotnet.exe' run --project .\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj -- image-bag-calibration '<REPO_ROOT>\artifacts\live-bag-calibration\client-33864-20260630-155016.png'
```

### 6.4 最新验证结果

- 构建：0 警告，0 错误。
- `bag-cleanup`：PASS。扫描、旧清单导入、保留/出售/未知保护、DryRun 均通过。
- `normal-hang`：PASS。外置按键、游戏内置监控分支、回血回蓝、技能节奏和回点子链路均通过诊断。
- `return-points`：PASS。地图 22 个、坐标 370 个，普通回点、土灵回点、死亡回点、卡点恢复均通过。
- `utility`：PASS。便捷动作序列兼容。
- `ui-navigation`：PASS。目标模式选择、横向模块、页面绑定正常。
- `live-bag-calibration`：PASS。当前现场背包未打开，返回 `NOT_DETECTED`，符合安全预期。
- `image-bag-calibration`：PASS。背包打开截图校准结果：`(1757,801) 10x6`，格 `32x32`，间距 `3/3`，预览占用 44、空格 16。

证据文件：

- 背包打开截图：`<REPO_ROOT>\artifacts\live-bag-calibration\client-33864-20260630-155016.png`
- 网格叠加图：`<REPO_ROOT>\artifacts\image-bag-calibration\client-33864-20260630-155016-20260630-190206-grid.png`
- 校准证据 JSON：`<REPO_ROOT>\artifacts\image-bag-calibration\client-33864-20260630-155016-20260630-190206.json`
- 当前现场只读证据 JSON：`<REPO_ROOT>\artifacts\live-bag-calibration\bag-calibration-33864-20260630-190207.json`
