# 龙伴行目标模式

## 总目标

完成一个真实可用的“龙伴行”32 位稳定版客户端。

目标不是重写玩法，也不是自由发挥新逻辑，而是在新前端、新配置、新日志、新交互下，尽量复刻并迁移旧主程序已经可用的核心能力，同时移除旧登录授权链、旧账号体系、旧命名和旧前端交互负担。

第一阶段只聚焦一条主线：

1. 挂机 / 按键打怪
2. 打死换怪
3. 玩家目标保护
4. 血蓝状态识别
5. 吃药回血 / 吃药回蓝
6. 血迹回蓝 / 共生回血
7. 宠物出战 / 宠物回血
8. 自动回点
9. 死亡回点
10. 卡点恢复
11. 清包只读识别和候选列表
12. 游戏内置挂机开关识别与触发
13. 只读运行态读取作为状态增强来源

暂不发散到抢小、抢反、马贼、生活、跑商、帮贡、师门、题库等二期功能。

## 文档依据

执行时必须参考并保持一致：

- `<REPO_ROOT>\docs\architecture\ADR-001-TechnologyStack.md`
- `<REPO_ROOT>\docs\architecture\ADR-002-CombatBackend.md`
- `<REPO_ROOT>\docs\architecture\ADR-003-HangReturnPointComposition.md`
- `<REPO_ROOT>\docs\architecture\ADR-005-CombatRegionCenter.md`
- `<REPO_ROOT>\docs\architecture\ADR-006-ResolutionProfile.md`
- `<REPO_ROOT>\docs\architecture\ADR-007-NoMemoryWriteAndRiskReducedGrab.md`
- `<REPO_ROOT>\docs\architecture\LongBanXing_ReadOnlyMemoryResearchPlan.md`
- `<REPO_ROOT>\docs\architecture\LongBanXing_RuntimeState_TargetMode.md`
- `<REPO_ROOT>\docs\modules\BlueDiamond_FunctionLogicMap.md`
- `<REPO_ROOT>\docs\migration\BlueDiamond_ReusePlan.md`
- `<REPO_ROOT>\docs\runtime\ReadOnlyProbeDiagnostics.md`
- `<REPO_ROOT>\docs\runtime\ReadOnlyMemoryCapabilityDiagnostics.md`
- `<REPO_ROOT>\docs\runtime\ReadOnlyCoordinateCandidatesDiagnostics.md`
- `<REPO_ROOT>\docs\runtime\ReadOnlyCoordinateNumericCandidatesDiagnostics.md`
- `<REPO_ROOT>\docs\runtime\ReadOnlyCoordinateNumericStabilityDiagnostics.md`
- `<REPO_ROOT>\docs\runtime\RuntimeFieldCandidatesDiagnostics.md`
- `<REPO_ROOT>\docs\runtime\RuntimeFieldStabilityDiagnostics.md`
- `<REPO_ROOT>\docs\runtime\CombatRecognitionDiagnostics.md`
- `<REPO_ROOT>\docs\runtime\ReturnPointDiagnostics.md`
- `<REPO_ROOT>\docs\runtime\BagCleanupDiagnostics.md`

如果代码实现与上述文档冲突，必须修正文档或代码，使二者一致；不允许口头承诺与代码脱节。

## 运行边界

1. 只处理龙伴行项目、自有旧程序、自有授权数据、自有配置和本地采样资产。
2. 所有代码放在：`<REPO_ROOT>`。
3. 不修改原始旧程序和授权数据目录。
4. 不引入旧登录、旧授权 DLL、旧账号体系。
5. 不在前端暴露旧软件名称、旧授权信息、旧账号信息。
6. 禁止内存写入、DLL 注入、Hook、客户端补丁、封包修改、绕过第三方安全组件。
7. 允许做“只读运行态读取”研究，但必须可关闭、有日志、有 JSON/截图证据，并且失败时自动回退到截图、图色、OCR、模板或基础按键逻辑。
8. 只读运行态读取先作为状态增强来源，不默认接管核心业务；必须经过诊断和验收后再逐项接入。

## 技术路线

继续使用：

- `.NET 8`
- `WPF`
- `x86`
- 服务层 + ViewModel + 前端展示分离

核心边界：

- UI 只做展示、输入、状态绑定。
- ViewModel 只调用 Contracts 接口。
- 业务逻辑只能写在服务和控制器。
- `NormalHangController` 负责挂机主循环。
- 回点逻辑独立服务化，但作为挂机能力组合使用。
- 清包逻辑独立服务化，默认只读分析。
- 运行态读取必须通过统一接口，不允许业务代码直接读进程。
- 日志统一走 `RuntimeLogService`。
- 配置统一走配置服务，修改后自动保存到当前方案。

## 只读运行态读取目标模式

新增统一运行状态接口：

```text
IRuntimeStateProvider
  - ScreenshotOcrRuntimeStateProvider
  - StaticAssetRuntimeStateProvider
  - UserSampleRuntimeStateProvider
  - ReadOnlyRuntimeStateProvider
```

统一输出 `RuntimeSnapshot`：

- 人物状态：名称、血量百分比、蓝量百分比、是否死亡、门派/职业信息。
- 目标状态：名称、血量百分比、是否玩家、是否怪物、是否宠物、是否可攻击、是否丢失目标。
- 宠物状态：是否出战、宠物血量百分比、宠物技能状态。
- 场景状态：地图名、坐标 X/Y、是否副本、是否野外。
- 场景门控：场景是否可用、地图字段是否可用、坐标字段是否可用、是否允许进入回点业务、不可用原因。
- 背包状态：背包是否打开、格子占用、物品候选信息。

只读读取原则：

1. 优先读取稳定、可验证、不频繁漂移的数据。
2. 所有读取结果必须带来源、置信度和可用状态。
3. 读取失败时，不允许卡死业务。
4. 截图识别和只读读取冲突时，记录冲突日志，优先采用更可信来源。
5. 未验证的地址、偏移、结构不允许硬编码进通用配置。
6. 32 位客户端使用独立读取配置；64 位以后单独规划。
7. 每个客户端版本必须记录版本号、路径、SHA256 和字段可用状态。

### 阶段 A：只读进程证据

允许读取：

- PID
- 窗口句柄
- 进程路径
- 进程位数
- 客户区尺寸
- 版本号
- SHA256
- 启动时间
- 探针状态

不允许：

- 不读人物状态内存
- 不读目标状态内存
- 不读地图坐标内存
- 不读背包状态内存
- 不发送按键
- 不点击鼠标
- 不写内存
- 不注入
- 不 Hook

验收命令：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --no-build --project '<REPO_ROOT>\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj' -- readonly-probe
```

### 阶段 B：只读字段候选

允许建立候选字段清单，但不接入业务：

- 人物 HP / MP 候选
- 目标 HP 候选
- 目标类型候选
- 地图名候选
- 坐标候选
- 死亡状态候选
- 背包状态候选

验收命令：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --no-build --project '<REPO_ROOT>\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj' -- runtime-field-candidates
```

要求：

- 每个字段必须带来源、置信度、当前状态。
- 每个字段必须标记不可用、不可信、可用、稳定。
- 每个字段必须能和截图 / OCR / 图色结果交叉验证。
- 不稳定字段不能进入自动化决策。
- 失败必须自动回退外部识别。

### 阶段 C：字段稳定性诊断

验收命令：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --no-build --project '<REPO_ROOT>\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj' -- runtime-field-stability
```

要求：

- 连续采样。
- 输出字段是否稳定。
- 输出字段变化范围。
- 输出 JSON 证据。
- 稳定前不能接入业务。

### 阶段 D：业务接入

只允许稳定字段接入：

- 打死换怪
- 玩家目标保护
- 回血回蓝
- 自动回点
- 死亡回点
- 清包候选

禁止接入：

- 不稳定地址
- 未经截图交叉验证字段
- 需要写内存才成立的能力
- 需要注入或 Hook 才成立的能力
- 会绕过第三方安全组件的能力

## 打怪目标模式

打怪循环必须按以下逻辑执行：

1. 读取当前快照。
2. 没有目标时，按选怪键。
3. 识别目标。
4. 判断目标是否可攻击。
5. 如果是玩家、宠物、不可攻击目标、灰名保护目标、黄名保护目标，则切换下一个目标。
6. 新目标确认可攻击后，普通攻击只触发一次。
7. 技能按独立计时器循环释放。
8. Buff 类技能 CD 到了就释放。
9. 目标血量归零或目标丢失后，重新选怪。
10. OCR / 图色 / 只读读取失败时，不阻塞基础打怪，可以退化为“选怪 + 普攻一次 + 技能计时器”。
11. 玩家选择“打死换怪”时，必须监控目标血条。
12. 玩家选择“抢怪模式”时，可以简化为快速选怪攻击，但仍必须做玩家保护。

普通攻击规则：

- 普通攻击不是技能循环。
- 选中新目标并确认可攻击后，只按一次普通攻击。
- 后续由技能计时器释放技能。
- 目标死亡、目标丢失或需要切换时，再进入下一轮选怪。

技能规则：

- 快捷键默认 F1-F10，不使用 F11/F12。
- 玩家可自定义为 QWER、数字键等。
- 第一行固定普通攻击。
- 后续技能可选择伤害、AOE、增益 Buff。
- 增益 Buff 按独立 CD 到点释放。
- 技能间隔按秒显示，内部统一换算为毫秒。

## 玩家目标保护

默认开启。

识别依据优先级：

1. 目标面板是否有人物头像。
2. 目标面板左侧是否存在门派标识区域。
3. 目标血条下方是否存在玩家特征条。
4. 只读运行态是否能稳定识别目标类型。
5. 名字颜色、面板结构、头像位置辅助判断。

不能只依赖蓝条和怒气条，因为玩家蓝量或怒气可能为空。

不能只依赖红色血条，因为怪物、BOSS、小头目也可能有红色血条。

只要判断为玩家，默认不攻击，立即切换目标，并写日志说明：

```text
目标保护：识别为玩家目标，已切换下一个目标。
```

## 状态与药品逻辑

状态识别：

- 人物血量百分比
- 人物蓝量百分比
- 宠物血量百分比
- 目标血量百分比

状态来源：

- 截图 / 图色 / OCR
- 用户采样资产
- 旧模板资产
- 只读运行态快照

动作逻辑：

- 吃药回血：人物血量低于阈值触发，药品公共 CD 默认 10 秒。
- 吃药回蓝：人物蓝量低于阈值触发，药品公共 CD 默认 10 秒。
- 血迹回蓝：人物蓝量低于阈值触发，宠物技能独立 CD 默认 30 秒。
- 共生回血：人物血量低于阈值触发，宠物技能独立 CD 默认 30 秒。
- 宠物回血：宠物血量低于阈值触发宠粮或配置动作。
- 宠物出战：默认出战当前宠物。

血迹 / 共生优先做鼠标点击宠物技能区域，不强依赖玩家把宠物技能拖进技能栏。

冷却规则：

- 吃药回血和吃药回蓝共用药品公共 CD，默认 10 秒。
- 血迹和共生按宠物技能独立 CD，默认 30 秒。
- CD 未到时不得重复触发，避免日志刷屏和动作卡死。
- 触发失败时写日志，但不能阻塞选怪和技能循环。

## 战斗方式自动判断

玩家可选择：

1. 自动判断
2. 游戏内置挂机
3. 程序按键打怪

自动判断策略：

- 野外优先游戏内置挂机。
- 副本优先程序外部按键打怪。
- 游戏内置挂机不可用时，回退程序按键打怪。
- 程序按键打怪识别不稳定时，退化为基础按键模式。

游戏内置挂机模式下：

- 龙伴行负责开启内置挂机。
- 龙伴行负责监控地图、坐标、死亡、卡点、回点。
- 龙伴行不发送选怪、普攻、技能循环。

程序按键打怪模式下：

- 龙伴行负责选怪。
- 龙伴行负责目标保护。
- 龙伴行负责普通攻击一次。
- 龙伴行负责技能计时器。
- 龙伴行负责回血回蓝、血迹、共生、宠物回血。
- 龙伴行负责回点、死亡、卡点恢复。

## 回点目标模式

回点分三类：

1. 挂机回点
2. 土灵回点
3. 死亡回点

必须完成：

- 坐标采集
- 地图识别
- 当前坐标识别
- 偏离距离判断
- 卡点判断
- 回点执行
- 回点后恢复挂机
- 死亡后复活 / 回城 / 回点流程

地图和坐标来源：

- 优先外部识别和用户采样。
- 可用时用只读运行态快照增强。
- 只读数据不稳定时，不触发自动回点。
- 地图未知时，只执行用户明确允许的本地图动作。

执行规则：

- 外部按键打怪：由龙伴行控制选怪、技能、回点。
- 游戏内置挂机：龙伴行只负责开启内置挂机、监控坐标、异常回点、死亡处理。
- 回点中暂停攻击、技能、回血回蓝。
- 回点完成后恢复原挂机模式。

## 死亡回点目标模式

必须识别：

- 死亡提示
- 复活界面
- 回城状态
- 当前地图
- 当前坐标

流程：

1. 检测死亡。
2. 停止攻击和技能。
3. 执行复活或回城动作。
4. 等待角色恢复可操作。
5. 判断地图和坐标。
6. 执行回点路线。
7. 到点后恢复挂机。

只读运行态可增强：

- 是否死亡。
- 当前地图。
- 当前坐标。

但未验证稳定前，死亡回点不得只依赖只读字段。

## 清包目标模式

清包先做安全版本：

1. 只识别背包格子。
2. 只生成候选列表。
3. 默认不自动出售。
4. 必须有白名单。
5. 未识别物品默认保护。
6. 贵重物品默认保护。
7. 任务物品默认保护。
8. 绑定物品默认保护。
9. 出售动作必须二次确认，不能默认执行。

数据来源：

- 背包截图 / 图色识别。
- 用户白名单 / 黑名单。
- 旧数据清单。
- 静态物品库。
- 可用时使用只读运行态快照辅助判断格子和物品状态。

安全规则：

- 只读阶段不出售。
- 未识别不出售。
- 有绑定标识不出售。
- 有任务标识不出售。
- 无白名单命中的物品不出售。
- 自动出售必须单独开关，并二次确认。

## 识别与分辨率目标

第一稳定分辨率先以当前常用窗口为基准：

- `1920x1080`
- `2560x1369`
- `1024x768`

必须支持分辨率画像：

- 每个分辨率有独立识别区域。
- 用户可采样校准。
- 识别区域必须能导出截图证据。
- 默认区域不准写死成唯一标准。
- 只读运行态读取不能替代截图校准，因为前端仍需要可视化证据。

识别区域中心必须包含：

- 人物头像区
- 人物血条
- 人物蓝条
- 宠物头像区
- 宠物血条
- 目标姓名栏
- 目标第一条血条
- 目标第二条特征条
- 目标第三条特征条
- 目标头像区
- 目标门派标识区
- 小地图地图名
- 小地图坐标
- 背包格子区

## 当前执行顺序

第一阶段只做以下内容，不插入其他大功能：

1. 修正打怪循环。
2. 完成玩家目标保护。
3. 完成血蓝 / 宠物 / 目标识别区域校准。
4. 完成只读运行态读取阶段 A 和阶段 B 诊断。
5. 完成回血回蓝 / 血迹 / 共生 / 宠物回血冷却逻辑。
6. 完成打死换怪。
7. 完成野外内置挂机 / 副本外部打怪的模式选择。
8. 完成自动回点。
9. 完成死亡回点。
10. 完成清包只读识别和候选列表。
11. 每一步都必须有诊断命令、日志和截图或 JSON 证据。

## 验收标准

验收时必须看到：

1. 绑定 32 位游戏窗口成功。
2. 启动后能稳定打怪。
3. 不会攻击玩家目标。
4. 目标死亡后能换怪。
5. 普通攻击只在新目标确认后触发一次。
6. 技能释放节奏稳定。
7. Buff 类技能按独立 CD 释放。
8. 血量低能回血。
9. 蓝量低能回蓝。
10. 血迹 / 共生不会因 CD 卡死。
11. 宠物没血有处理逻辑。
12. 偏离挂机点能回点。
13. 死亡后能进入死亡回点流程。
14. 清包能识别背包并生成候选，不误卖。
15. 所有配置实时保存。
16. 所有关键动作有日志。
17. 所有识别区域能截图验证。
18. `readonly-probe` 能生成只读进程证据。
19. `runtime-field-candidates` 能生成只读字段候选清单。
20. `runtime-field-stability` 能生成字段连续采样稳定性证据。
21. 只读运行态字段必须带来源、置信度和可用状态。
22. 只读读取失败时，业务能自动回退外部识别或基础按键逻辑。

## 诊断命令

构建：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build '<REPO_ROOT>\TlbbAgent.sln'
```

启动：

```powershell
& '<REPO_ROOT>\src\TlbbAgent.App\bin\Debug\net8.0-windows\TlbbAgent.App.exe'
```

只读进程证据：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --no-build --project '<REPO_ROOT>\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj' -- readonly-probe
```

只读字段候选：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --no-build --project '<REPO_ROOT>\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj' -- runtime-field-candidates
```

字段稳定性诊断：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --no-build --project '<REPO_ROOT>\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj' -- runtime-field-stability
```

场景字段诊断：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --no-build --project '<REPO_ROOT>\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj' -- scene-fields
```

运行态快照：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --no-build --project '<REPO_ROOT>\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj' -- runtime-snapshot
```

打怪诊断：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --no-build --project '<REPO_ROOT>\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj' -- normal-hang
```

回点诊断：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --no-build --project '<REPO_ROOT>\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj' -- return-points
```

清包诊断：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --no-build --project '<REPO_ROOT>\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj' -- bag-cleanup
```

整体就绪检查：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --no-build --project '<REPO_ROOT>\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj' -- readiness
```

## 执行要求

从现在开始，按这个目标模式执行。

不要再发散做抢小、抢反、马贼、生活、帮贡、师门等二期功能。

先把挂机、回点、死亡、清包这条主线做到真实可用。

只读运行态读取可以做，但必须先诊断、后验证、再接入；任何不稳定字段都不能进入自动化决策。

每次完成一个功能点，都要给出：

- 修改范围
- 运行命令
- 日志证据
- 截图或 JSON 证据
- 当前限制
- 下一步动作
