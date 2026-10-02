param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [switch]$Build,
    [switch]$CloseLaunched
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
$appProject = Join-Path $Root "src\TlbbAgent.App\TlbbAgent.App.csproj"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outDir = Join-Path $Root "artifacts\agent-ui-p0-smoke\$stamp"
$jsonPath = Join-Path $outDir "agent-ui-p0-smoke.json"
$mdPath = Join-Path $outDir "agent-ui-p0-smoke.md"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

if ($Build) {
    $buildOutput = & $dotnet build $appProject -c Debug --no-restore --nologo 2>&1
    $buildText = ($buildOutput | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    Set-Content -LiteralPath (Join-Path $outDir "build.txt") -Value $buildText -Encoding UTF8
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed. See $(Join-Path $outDir 'build.txt')"
    }
}

function Add-XamlNodeNames {
    param(
        [System.Xml.XmlNode]$Node,
        [System.Collections.ArrayList]$Names
    )

    if ($Node.NodeType -ne [System.Xml.XmlNodeType]::Element) {
        return
    }

    $visibility = $Node.Attributes["Visibility"]
    if ($visibility -and $visibility.Value -eq "Collapsed") {
        return
    }

    foreach ($attribute in $Node.Attributes) {
        if ($attribute.Name -notin @("Text", "Content", "Header", "AutomationProperties.Name")) {
            continue
        }

        $value = $attribute.Value
        if ([string]::IsNullOrWhiteSpace($value) -or $value.StartsWith("{")) {
            continue
        }

        if (-not $Names.Contains($value)) {
            [void]$Names.Add($value)
        }
    }

    foreach ($child in $Node.ChildNodes) {
        Add-XamlNodeNames -Node $child -Names $Names
    }
}

function Get-XamlNames {
    param(
        [string]$Path,
        [string]$VisibilityBinding = ""
    )

    [xml]$document = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    $roots = @($document.DocumentElement)
    if (-not [string]::IsNullOrWhiteSpace($VisibilityBinding)) {
        $roots = @(
            $document.SelectNodes("//*") |
                Where-Object {
                    $attribute = $_.Attributes["Visibility"]
                    $attribute -and $attribute.Value.Contains($VisibilityBinding)
                })
    }

    $names = New-Object System.Collections.ArrayList
    foreach ($rootNode in $roots) {
        Add-XamlNodeNames -Node $rootNode -Names $names
    }

    @($names)
}

function Test-PageContract {
    param(
        [string]$Page,
        [string[]]$Names,
        [string[]]$Expected,
        [string[]]$Forbidden
    )

    $joined = $Names -join "`n"
    $missing = @($Expected | Where-Object { $joined -notmatch [regex]::Escape($_) })
    $visibleForbidden = @($Forbidden | Where-Object { $joined -match [regex]::Escape($_) })
    [pscustomobject]@{
        page = $Page
        ok = $missing.Count -eq 0 -and $visibleForbidden.Count -eq 0
        missing = $missing
        forbiddenVisible = $visibleForbidden
        visibleNames = $Names
    }
}

$results = New-Object System.Collections.ArrayList
$globalConfigPath = Join-Path $Root "src\TlbbAgent.App\Views\GlobalConfigPage.xaml"
$hangPagePath = Join-Path $Root "src\TlbbAgent.App\Views\HangPage.xaml"
$hangViewModelPath = Join-Path $Root "src\TlbbAgent.App\ViewModels\HangPageViewModel.cs"
$mainViewModelPath = Join-Path $Root "src\TlbbAgent.App\ViewModels\MainViewModel.cs"
$globalContractPath = Join-Path $Root "src\TlbbAgent.Contracts\GlobalConfig.cs"
$shoutPagePath = Join-Path $Root "src\TlbbAgent.App\Views\ShoutPage.xaml"
$pageViewModelsPath = Join-Path $Root "src\TlbbAgent.App\ViewModels\PageViewModels.cs"
$bossPagePath = Join-Path $Root "src\TlbbAgent.App\Views\BossModePage.xaml"
$dungeonPagePath = Join-Path $Root "src\TlbbAgent.App\Views\DungeonModePage.xaml"
$activityPagePath = Join-Path $Root "src\TlbbAgent.App\Views\ActivityCenterPage.xaml"
$activityViewModelPath = Join-Path $Root "src\TlbbAgent.App\ViewModels\ActivityCenterPageViewModel.cs"
$cleanupPagePath = Join-Path $Root "src\TlbbAgent.App\Views\AutoCleanupPage.xaml"
$cleanupViewModelPath = Join-Path $Root "src\TlbbAgent.App\ViewModels\HangLifecyclePageViewModels.cs"
$materialPagePath = Join-Path $Root "src\TlbbAgent.App\Views\MaterialProcessingPage.xaml"
$materialControllerPath = Join-Path $Root "src\TlbbAgent.Core\Automation\MaterialCraftController.cs"
$taskPagePath = Join-Path $Root "src\TlbbAgent.App\Views\TaskAutomationPage.xaml"
$taskViewModelPath = Join-Path $Root "src\TlbbAgent.App\ViewModels\TaskAutomationPageViewModel.cs"
$quizPagePath = Join-Path $Root "src\TlbbAgent.App\Views\QuizAssistantPage.xaml"
$inputServicePath = Join-Path $Root "src\TlbbAgent.Core\InputService.cs"
$logPanelPath = Join-Path $Root "src\TlbbAgent.App\Views\LogPanel.xaml"
$appXamlPath = Join-Path $Root "src\TlbbAgent.App\App.xaml"
$mainWindowPath = Join-Path $Root "src\TlbbAgent.App\MainWindow.xaml"
$featureKeysPath = Join-Path $Root "src\TlbbAgent.Contracts\FeatureKeys.cs"
$moduleCatalogPath = Join-Path $Root "src\TlbbAgent.Core\ModuleCatalog.cs"
$shoutControllerPath = Join-Path $Root "src\TlbbAgent.Core\Automation\ShoutController.cs"
$shoutRuntimePath = Join-Path $Root "src\TlbbAgent.Core\ShoutQueueRuntimeService.cs"
$logContractPath = Join-Path $Root "src\TlbbAgent.Contracts\Models.cs"
$logServicePath = Join-Path $Root "src\TlbbAgent.Core\LogService.cs"
$logPrivacyPath = Join-Path $Root "src\TlbbAgent.Core\UserLogPrivacyPolicy.cs"
$telemetryPath = Join-Path $Root "src\TlbbAgent.Core\CombatLoopTelemetryService.cs"
$logViewModelPath = Join-Path $Root "src\TlbbAgent.App\ViewModels\LogPanelViewModel.cs"
$appCodePath = Join-Path $Root "src\TlbbAgent.App\App.xaml.cs"
$appProjectPath = Join-Path $Root "src\TlbbAgent.App\TlbbAgent.App.csproj"
$themePath = Join-Path $Root "src\TlbbAgent.App\Themes\Theme.xaml"
$brandLogoPath = Join-Path $Root "src\TlbbAgent.App\Assets\Brand\official-xtl-primary-logo.png"
$brandPosterPath = Join-Path $Root "src\TlbbAgent.App\Assets\Brand\tlbb-agent-poster.png"
$brandIconPath = Join-Path $Root "src\TlbbAgent.App\Assets\Brand\tlbb-agent-v2.ico"

$mainWindowBrandSource = Get-Content -LiteralPath $mainWindowPath -Raw -Encoding UTF8
$appProjectBrandSource = Get-Content -LiteralPath $appProjectPath -Raw -Encoding UTF8
$themeBrandSource = Get-Content -LiteralPath $themePath -Raw -Encoding UTF8
$brandSource = @($mainWindowBrandSource, $appProjectBrandSource, $themeBrandSource) -join "`n"
$mainWindowBrandRules = @(
    'Title="TLBB Agent"',
    'Icon="Assets/Brand/tlbb-agent-v2.ico"',
    'Text="TLBB Agent"',
    'Text="新一代的AI智能辅助系统"',
    'Source="Assets/Brand/official-xtl-primary-logo.png"',
    'Source="Assets/Brand/tlbb-agent-poster.png"',
    'Fill="{StaticResource BrandPosterOverlayBrush}"',
    'Content="−" FontSize="17" Click="OnMinimize"',
    'Content="□" FontSize="15" Click="OnMaximizeRestore"',
    'Content="×" FontSize="18" Click="OnClose"'
)
$themeBrandRules = @(
    'x:Key="BrandPosterOverlayBrush"'
)
$appProjectBrandRules = @(
    '<AssemblyName>TLBB Agent</AssemblyName>',
    '<Company>TLBB Agent</Company>',
    '<ApplicationIcon>Assets\Brand\tlbb-agent-v2.ico</ApplicationIcon>',
    '<Resource Include="Assets\Brand\official-xtl-primary-logo.png" />',
    '<Resource Include="Assets\Brand\tlbb-agent-v2.ico" />'
)
$missingBrandRules = @(
    $mainWindowBrandRules |
        Where-Object { -not $mainWindowBrandSource.Contains($_) } |
        ForEach-Object { "MainWindow: $_" }
    $themeBrandRules |
        Where-Object { -not $themeBrandSource.Contains($_) } |
        ForEach-Object { "Theme: $_" }
    $appProjectBrandRules |
        Where-Object { -not $appProjectBrandSource.Contains($_) } |
        ForEach-Object { "AppProject: $_" }
)
$missingBrandRules += @(
    @($brandLogoPath, $brandPosterPath, $brandIconPath) |
        Where-Object { -not (Test-Path -LiteralPath $_) } |
        ForEach-Object { "资源文件: $_" }
)
$legacyBrandRules = @('Title="龙伴行"', 'Text="龙伴行"', 'AI Agent 控制中心', '<AssemblyName>龙伴行</AssemblyName>')
$visibleLegacyBrandRules = @($legacyBrandRules | Where-Object { $brandSource.Contains($_) })
[void]$results.Add([pscustomobject]@{
    page = "品牌资源"
    ok = $missingBrandRules.Count -eq 0 -and $visibleLegacyBrandRules.Count -eq 0
    missing = $missingBrandRules
    forbiddenVisible = $visibleLegacyBrandRules
    visibleNames = @()
})

$windowSelectionRules = @(
    'Content="{Binding SelectButtonText}"',
    'Command="{Binding DataContext.SelectGameWindowCommand, RelativeSource={RelativeSource AncestorType=Window}}"',
    'Text="{Binding WindowScanStatus}"'
)
$missingWindowSelectionRules = @($windowSelectionRules | Where-Object { -not $mainWindowBrandSource.Contains($_) })
$mainViewModelSource = Get-Content -LiteralPath $mainViewModelPath -Raw -Encoding UTF8
$forbiddenAutoBindingRules = @(
    'var preferred = windows.FirstOrDefault',
    '_binding.BindByHandle(preferred.Hwnd)'
)
$visibleAutoBindingRules = @($forbiddenAutoBindingRules | Where-Object { $mainViewModelSource.Contains($_) })
[void]$results.Add([pscustomobject]@{
    page = "游戏窗口选择"
    ok = $missingWindowSelectionRules.Count -eq 0 -and $visibleAutoBindingRules.Count -eq 0
    missing = $missingWindowSelectionRules
    forbiddenVisible = $visibleAutoBindingRules
    visibleNames = @("扫描候选", "明确选择", "绑定状态")
})

$twoLevelShellRules = @(
    'CornerRadius="16,0,0,0"',
    'Style="{StaticResource FlatShellButton}"',
    'AutomationProperties.Name="账户菜单"',
    'Text="{Binding UserDisplayName}"',
    'IsOpen="{Binding IsAccountMenuOpen, Mode=TwoWay}"'
)
$missingTwoLevelShellRules = @($twoLevelShellRules | Where-Object { -not $mainWindowBrandSource.Contains($_) })
$forbiddenShellDividerRules = @(
    'BorderThickness="0,1,1,0"',
    'BorderThickness="0,1,0,1"',
    'BorderThickness="0,0,0,1"'
)
$visibleShellDividerRules = @($forbiddenShellDividerRules | Where-Object { $mainWindowBrandSource.Contains($_) })
$userSessionRules = @(
    'private readonly IUserSessionService _userSession;',
    'public string UserDisplayName',
    'private void ToggleAccountMenu()',
    'private async Task BeginUserLogin()'
)
$missingUserSessionRules = @($userSessionRules | Where-Object { -not $mainViewModelSource.Contains($_) })
[void]$results.Add([pscustomobject]@{
    page = "两级主壳与账户入口"
    ok = $missingTwoLevelShellRules.Count -eq 0 -and $visibleShellDividerRules.Count -eq 0 -and $missingUserSessionRules.Count -eq 0
    missing = @($missingTwoLevelShellRules + $missingUserSessionRules)
    forbiddenVisible = $visibleShellDividerRules
    visibleNames = @("品牌与窗口层", "控制台层", "账户菜单")
})

$featureKeysSource = Get-Content -LiteralPath $featureKeysPath -Raw -Encoding UTF8
$moduleCatalogSource = Get-Content -LiteralPath $moduleCatalogPath -Raw -Encoding UTF8
$routeConsumerSource = @(
    Get-Content -LiteralPath $mainViewModelPath -Raw -Encoding UTF8
    $moduleCatalogSource
    Get-Content -LiteralPath $shoutControllerPath -Raw -Encoding UTF8
    Get-Content -LiteralPath $shoutRuntimePath -Raw -Encoding UTF8
) -join "`n"
$formalFeatureDeclarations = @(
    'public const string RoleCommon = "role.common";',
    'public const string LifeShout = "life.shout";',
    'public const string HangNormal = "hang.normal";',
    'public const string HangBoss = "hang.boss";',
    'public const string HangDungeon = "hang.dungeon";',
    'public const string HangCleanup = "hang.cleanup";',
    'public const string HangMaterial = "hang.material";',
    'public const string ActivityRebel = "activity.rebel";',
    'public const string ActivityXiaoxiao = "activity.xiaoxiao";',
    'public const string ActivityMonk = "activity.monk";',
    'public const string LifeTreasureMap = "life.treasure-map";',
    'public const string LifeMining = "life.mining";',
    'public const string LifeHerb = "life.herb";',
    'public const string LifeTrade = "life.trade";',
    'public const string TaskDaily = "task.daily";',
    'public const string QuizAssistant = "quiz.assistant";'
)
$catalogFeatureReferences = @(
    'Key = FeatureKeys.LifeShout',
    'Key = FeatureKeys.HangNormal',
    'Key = FeatureKeys.HangBoss',
    'Key = FeatureKeys.HangDungeon',
    'Key = FeatureKeys.HangCleanup',
    'Key = FeatureKeys.HangMaterial',
    'Key = FeatureKeys.ActivityRebel',
    'Key = FeatureKeys.ActivityXiaoxiao',
    'Key = FeatureKeys.ActivityMonk',
    'Key = FeatureKeys.LifeTreasureMap',
    'Key = FeatureKeys.LifeMining',
    'Key = FeatureKeys.LifeHerb',
    'Key = FeatureKeys.LifeTrade',
    'Key = FeatureKeys.TaskDaily',
    'Key = FeatureKeys.QuizAssistant'
)
$routeFeatureRules = @(
    'private static string ControllerKeyForMode(string key) => key switch',
    'FeatureKeys.HangBoss => FeatureKeys.HangNormal',
    'FeatureKeys.HangNormal => new HangPageViewModel(',
    'FeatureKeys.HangBoss => new BossModePageViewModel(_global, _targetPolicyCatalog, OpenHomeTab)',
    'FeatureKeys.HangDungeon => new DungeonModePageViewModel()',
    'FeatureKeys.HangCleanup => new AutoCleanupPageViewModel(_utility)',
    'FeatureKeys.HangMaterial => new MaterialProcessingPageViewModel(',
    'FeatureKeys.LifeShout => new ShoutPageViewModel(_shoutConfig, _shoutController)',
    'FeatureKeys.TaskDaily => new TaskAutomationPageViewModel(_global)',
    'FeatureKeys.QuizAssistant => new QuizAssistantPageViewModel()',
    'private const string FeatureKey = FeatureKeys.LifeShout;'
)
$missingFormalFeatureRules = @()
$missingFormalFeatureRules += @($formalFeatureDeclarations | Where-Object { -not $featureKeysSource.Contains($_) } | ForEach-Object { "FeatureKeys: $_" })
$missingFormalFeatureRules += @($catalogFeatureReferences | Where-Object { -not $moduleCatalogSource.Contains($_) } | ForEach-Object { "ModuleCatalog: $_" })
$missingFormalFeatureRules += @($routeFeatureRules | Where-Object { -not $routeConsumerSource.Contains($_) } | ForEach-Object { "Routing: $_" })

$legacyRouteAliases = @(
    '"hang.horse"', '"hang.utility"', '"life.craft"', '"activity.center"',
    '"act.grab"', '"act.rebel"', '"act.monk"', '"life.map"', '"life.mine"',
    '"life.collect"', '"task.school"', '"task.quiz"', '"utility.shout"'
)
$formalRouteLiterals = @(
    '"role.common"', '"life.shout"', '"hang.normal"', '"hang.boss"', '"hang.dungeon"',
    '"hang.cleanup"', '"hang.material"', '"activity.rebel"', '"activity.xiaoxiao"',
    '"activity.monk"', '"life.treasure-map"', '"life.mining"', '"life.herb"',
    '"life.trade"', '"task.daily"', '"quiz.assistant"'
)
$forbiddenFormalRoutes = @()
$forbiddenFormalRoutes += @($legacyRouteAliases | Where-Object { $routeConsumerSource.Contains($_) } | ForEach-Object { "旧路由别名: $_" })
$forbiddenFormalRoutes += @($formalRouteLiterals | Where-Object { $routeConsumerSource.Contains($_) } | ForEach-Object { "绕过 FeatureKeys 的字面量: $_" })
[void]$results.Add([pscustomobject]@{
    page = "正式 FeatureKey"
    ok = $missingFormalFeatureRules.Count -eq 0 -and $forbiddenFormalRoutes.Count -eq 0
    missing = $missingFormalFeatureRules
    forbiddenVisible = $forbiddenFormalRoutes
    visibleNames = @()
})

$roleNames = Get-XamlNames -Path $globalConfigPath
[void]$results.Add((Test-PageContract -Page "角色设置" -Names $roleNames `
    -Expected @("门派", "选怪 / 切换目标", "内置挂机快捷键", "玩家操作保护", "恢复配置") `
    -Forbidden @("输入通道", "公共冷却", "点击坐标", "识别区", "标定", "识别 CD", "自动槽位", "采样")))

$keyModeNames = Get-XamlNames -Path $hangPagePath -VisibilityBinding "IsKeyMode"
[void]$results.Add((Test-PageContract -Page "运行态仅检查按键模式" -Names $keyModeNames `
    -Expected @("按键打怪", "攻击玩家", "峨眉加血", "给自己") `
    -Forbidden @("内置挂机监控", "自动切换目标", "无目标时自动选怪", "切怪方式", "定点巡逻", "给队友", "杀怪监控", "扫描间隔", "目标名称识别", "BOSS 模式白名单", "坐标", "Idle")))

$builtInRuleSource = @(
    Get-Content -LiteralPath $hangPagePath -Raw -Encoding UTF8
    Get-Content -LiteralPath $hangViewModelPath -Raw -Encoding UTF8
    Get-Content -LiteralPath $mainViewModelPath -Raw -Encoding UTF8
) -join "`n"
$builtInRules = @(
    "IsBuiltInMode",
    'BuiltInMonitorStatus => "未就绪"',
    "IsUnavailableBuiltInMonitorMode",
    'new HangCombatModeOption(CombatBackendPreference.InternalPlugin, "内置挂机", false, true)',
    'CanStart = window is not null',
    '&& !IsUnavailableBuiltInMonitorMode();',
    '_log.Warn(Module, "启动已取消：内挂持续监控尚未接入。", window.Id);'
)
$missingBuiltInRules = @($builtInRules | Where-Object { -not $builtInRuleSource.Contains($_) })
[void]$results.Add([pscustomobject]@{
    page = "内置挂机待接入静态规则"
    ok = $missingBuiltInRules.Count -eq 0
    missing = $missingBuiltInRules
    forbiddenVisible = @()
    visibleNames = @()
})

$keyModeSource = @(
    Get-Content -LiteralPath $hangViewModelPath -Raw -Encoding UTF8
    Get-Content -LiteralPath $globalContractPath -Raw -Encoding UTF8
) -join "`n"
$keyModeRules = @(
    'CombatBackendPreference { get; set; } = CombatBackendPreference.VisualModel',
    'new HangCombatModeOption(CombatBackendPreference.ExternalOcr, "按键打怪", true, false)',
    "IsKeyMode => !IsBuiltInMode"
)
$missingKeyModeRules = @($keyModeRules | Where-Object { -not $keyModeSource.Contains($_) })
[void]$results.Add([pscustomobject]@{
    page = "按键模式默认运行契约"
    ok = $missingKeyModeRules.Count -eq 0
    missing = $missingKeyModeRules
    forbiddenVisible = @()
    visibleNames = @()
})

$shoutNames = Get-XamlNames -Path $shoutPagePath
[void]$results.Add((Test-PageContract -Page "喊话" -Names $shoutNames `
    -Expected @("新增喊话", "顺序喊话") `
    -Forbidden @("多句循环", "每行一条内容", "发送间隔", "输入延迟", "确认延迟", "高级设置")))

$channelSource = Get-Content -LiteralPath $pageViewModelsPath -Raw -Encoding UTF8
$channelNames = @(
    "附近频道", "世界频道", "同城频道", "帮派频道", "同盟频道", "门派频道", "队伍频道",
    'public string Title => $"喊话 {Number}"'
)
$missingChannels = @($channelNames | Where-Object { -not $channelSource.Contains($_) })
[void]$results.Add([pscustomobject]@{
    page = "喊话频道配置"
    ok = $missingChannels.Count -eq 0
    missing = $missingChannels
    forbiddenVisible = @()
    visibleNames = @()
})

$globalConfigXaml = Get-Content -LiteralPath $globalConfigPath -Raw -Encoding UTF8
$sliderCount = ([regex]::Matches($globalConfigXaml, '<Slider\b')).Count
[void]$results.Add([pscustomobject]@{
    page = "角色配置结构"
    ok = $sliderCount -eq 0
    missing = @()
    forbiddenVisible = @($(if ($sliderCount -gt 0) { "Slider=$sliderCount" }))
    visibleNames = @()
})

$skillEditorSource = @(
    $globalConfigXaml
    $channelSource
) -join "`n"
$skillEditorRules = @(
    'Visibility="{Binding IsSkillEditorOpen, Converter={StaticResource BoolToVis}}"',
    'Command="{Binding ToggleSkillEditorCommand}"',
    '[ObservableProperty] private bool _isSkillEditorOpen;',
    'public string SkillEditorButtonText => IsSkillEditorOpen ? "收起" : "管理";'
)
$missingSkillEditorRules = @($skillEditorRules | Where-Object { -not $skillEditorSource.Contains($_) })
$forbiddenSkillEditorRules = @(
    'Text="类型"',
    'Text="间隔"',
    'Text="条件"',
    '_isSkillEditorOpen = true'
)
$visibleForbiddenSkillEditorRules = @($forbiddenSkillEditorRules | Where-Object { $skillEditorSource.Contains($_) })
[void]$results.Add([pscustomobject]@{
    page = "技能编辑收敛"
    ok = $missingSkillEditorRules.Count -eq 0 -and $visibleForbiddenSkillEditorRules.Count -eq 0
    missing = $missingSkillEditorRules
    forbiddenVisible = $visibleForbiddenSkillEditorRules
    visibleNames = @()
})

$bossNames = Get-XamlNames -Path $bossPagePath
[void]$results.Add((Test-PageContract -Page "BOSS 状态页" -Names $bossNames `
    -Expected @("BOSS 模式", "启用", "运行能力", "BOSS 目标数据", "管理目标策略") `
    -Forbidden @("坐标", "扫描", "识别阈值", "技能时序", "队伍检查", "路线")))

$dungeonNames = Get-XamlNames -Path $dungeonPagePath
[void]$results.Add((Test-PageContract -Page "副本状态页" -Names $dungeonNames `
    -Expected @("状态", "当前副本", "当前阶段") `
    -Forbidden @("坐标", "扫描", "识别阈值", "脚本步骤", "队伍编号", "路线")))

$activityNames = Get-XamlNames -Path $activityPagePath
$activitySource = @(
    Get-Content -LiteralPath $activityPagePath -Raw -Encoding UTF8
    Get-Content -LiteralPath $activityViewModelPath -Raw -Encoding UTF8
) -join "`n"
[void]$results.Add((Test-PageContract -Page "活动中心" -Names $activityNames `
    -Expected @("活动", "找点方式", "状态", "启用") `
    -Forbidden @("坐标", "扫描间隔", "点击延时", "识别阈值", "执行按钮", "路线")))
$activityRules = @("自动反贼", "自动宵小", "自动和尚", "自动找点", "手动带到刷新点", 'private string _status = "未就绪"', 'public override string ToString() => Display;')
$missingActivityRules = @($activityRules | Where-Object { -not $activitySource.Contains($_) })
[void]$results.Add([pscustomobject]@{
    page = "活动中心配置"
    ok = $missingActivityRules.Count -eq 0
    missing = $missingActivityRules
    forbiddenVisible = @()
    visibleNames = @()
})

$cleanupNames = Get-XamlNames -Path $cleanupPagePath
[void]$results.Add((Test-PageContract -Page "自动清包" -Names $cleanupNames `
    -Expected @("自动清包", "清包方式", "回城地点", "只生成候选", "未知物品保护", "允许出售名单", "保留名单") `
    -Forbidden @("开始清包", "坐标", "扫描", "点击", "阈值", "输入通道", "步骤")))

$materialNames = Get-XamlNames -Path $materialPagePath
[void]$results.Add((Test-PageContract -Page "材料加工" -Names $materialNames `
    -Expected @("材料加工", "自动材料加工", "加工周期", "分钟") `
    -Forbidden @("开始加工", "坐标", "扫描", "点击", "阈值", "输入通道", "步骤", "批次", "配方")))

$taskNames = Get-XamlNames -Path $taskPagePath
$taskSource = @(
    Get-Content -LiteralPath $taskPagePath -Raw -Encoding UTF8
    Get-Content -LiteralPath $taskViewModelPath -Raw -Encoding UTF8
) -join "`n"
[void]$results.Add((Test-PageContract -Page "日常任务" -Names $taskNames `
    -Expected @("日常任务", "执行顺序", "师门任务", "打怪", "送信", "抓宝宝", "买物品") `
    -Forbidden @("开始执行", "添加步骤", "坐标", "扫描", "点击", "输入通道", "步骤队列")))
$taskRules = @("帮贡任务", "新鸳鸯蝴蝶梦", "百变脸谱", "一千零一个愿望", "MoveTask")
$missingTaskRules = @($taskRules | Where-Object { -not $taskSource.Contains($_) })
[void]$results.Add([pscustomobject]@{
    page = "日常任务清单"
    ok = $missingTaskRules.Count -eq 0
    missing = $missingTaskRules
    forbiddenVisible = @()
    visibleNames = @()
})

$quizNames = Get-XamlNames -Path $quizPagePath
[void]$results.Add((Test-PageContract -Page "答题助手" -Names $quizNames `
    -Expected @("服务状态", "剩余次数", "自动答题") `
    -Forbidden @("坐标", "扫描", "点击", "阈值", "输入通道", "题库路径")))
$quizSource = Get-Content -LiteralPath $quizPagePath -Raw -Encoding UTF8
$quizBindingRule = 'IsChecked="{Binding IsEnabled, Mode=OneWay}"'
[void]$results.Add([pscustomobject]@{
    page = "答题助手只读状态绑定"
    ok = $quizSource.Contains($quizBindingRule)
    missing = @($(if (-not $quizSource.Contains($quizBindingRule)) { $quizBindingRule }))
    forbiddenVisible = @()
    visibleNames = @()
})

$mainSource = @(
    Get-Content -LiteralPath $mainViewModelPath -Raw -Encoding UTF8
    Get-Content -LiteralPath $mainWindowPath -Raw -Encoding UTF8
    Get-Content -LiteralPath $materialControllerPath -Raw -Encoding UTF8
) -join "`n"
$mainRules = @(
    'SelectedRunMode.CanRun',
    'DisplayMemberPath="RunLabel"',
    'RunModes.Contains(value)',
    'DataTrigger Binding="{Binding IsCurrent}" Value="True"',
    "IsAvailable = _shoutController.IsChannelRoutingReady",
    'FeatureKeys.HangCleanup => new AutoCleanupPageViewModel(_utility)',
    'FeatureKeys.HangMaterial => new MaterialProcessingPageViewModel(',
    'CanRun = module.Key is FeatureKeys.HangNormal or FeatureKeys.HangBoss or FeatureKeys.HangMaterial',
    'public override string Key => FeatureKeys.HangMaterial;',
    'UiRouteKeys.ActivityCenter => _activityPage ??= new ActivityCenterPageViewModel(_global)',
    'FeatureKeys.TaskDaily => new TaskAutomationPageViewModel(_global)',
    'FeatureKeys.QuizAssistant => new QuizAssistantPageViewModel()',
    'InputYieldStatusText = "玩家操作保护：可用"'
)
$missingMainRules = @($mainRules | Where-Object { -not $mainSource.Contains($_) })
$devUiDebugOnly = [regex]::IsMatch(
    $mainSource,
    '(?s)#if DEBUG\s+if \(string\.Equals\(Environment\.GetEnvironmentVariable\("TLBB_AGENT_DEV_UI"\).*?Tabs\.Add\(data\);\s*}\s*#endif')
if (-not $devUiDebugOnly) {
    $missingMainRules += "TLBB_AGENT_DEV_UI 必须位于 #if DEBUG 块内"
}
[void]$results.Add([pscustomobject]@{
    page = "主壳能力边界"
    ok = $missingMainRules.Count -eq 0
    missing = $missingMainRules
    forbiddenVisible = @()
    visibleNames = @()
})

$mainWindowNames = Get-XamlNames -Path $mainWindowPath
[void]$results.Add((Test-PageContract -Page "主工作区" -Names $mainWindowNames `
    -Expected @("游戏窗口", "扫描游戏窗口", "角色记忆", "当前任务", "暂停", "停止全部", "首次绑定") `
    -Forbidden @("角色会话", "待接入", "修改会自动保存", "输入通道", "坐标", "PID")))

$sessionRules = @(
    'public void ApplyIdentity(CharacterIdentityRecord identity, string roleSessionId)',
    'private string _identityStatusText = "正在识别角色"',
    'private RunState _runState = RunState.Idle',
    'private void BeginRoleIdentityResolution(BoundGameWindow window, WindowSessionViewModel session)',
    'private void ActivateMatchedRole('
)
$missingSessionRules = @($sessionRules | Where-Object { -not $mainSource.Contains($_) })
[void]$results.Add([pscustomobject]@{
    page = "窗口会话真实性"
    ok = $missingSessionRules.Count -eq 0
    missing = $missingSessionRules
    forbiddenVisible = @()
    visibleNames = @()
})

$inputSource = Get-Content -LiteralPath $inputServicePath -Raw -Encoding UTF8
$mouseRules = @("WH_MOUSE_LL", "WM_LBUTTONDOWN", "WM_RBUTTONDOWN", "WM_MBUTTONDOWN", "WM_XBUTTONDOWN", "LLMHF_INJECTED", 'Scope: "target-window"')
$missingMouseRules = @($mouseRules | Where-Object { -not $inputSource.Contains($_) })
$forbiddenInputRules = @("GetLastInputInfo", "LASTINPUTINFO") | Where-Object { $inputSource.Contains($_) }
[void]$results.Add([pscustomobject]@{
    page = "玩家鼠标操作保护"
    ok = $missingMouseRules.Count -eq 0 -and @($forbiddenInputRules).Count -eq 0
    missing = $missingMouseRules
    forbiddenVisible = @($forbiddenInputRules)
    visibleNames = @()
})

$logSource = Get-Content -LiteralPath $logPanelPath -Raw -Encoding UTF8
$collapsedRawSummary = $logSource.Contains('Text="{Binding LatestSummary}"')
[void]$results.Add([pscustomobject]@{
    page = "日志抽屉"
    ok = -not $collapsedRawSummary
    missing = @()
    forbiddenVisible = @($(if ($collapsedRawSummary) { "折叠态原始日志摘要" }))
    visibleNames = @()
})

$logContractSource = Get-Content -LiteralPath $logContractPath -Raw -Encoding UTF8
$logServiceSource = Get-Content -LiteralPath $logServicePath -Raw -Encoding UTF8
$logPrivacySource = Get-Content -LiteralPath $logPrivacyPath -Raw -Encoding UTF8
$telemetrySource = Get-Content -LiteralPath $telemetryPath -Raw -Encoding UTF8
$logViewModelSource = Get-Content -LiteralPath $logViewModelPath -Raw -Encoding UTF8
$logPanelSource = Get-Content -LiteralPath $logPanelPath -Raw -Encoding UTF8
$appCodeSource = Get-Content -LiteralPath $appCodePath -Raw -Encoding UTF8
$structuredLogContractRules = @(
    'public enum LogAudience',
    'public LogAudience Audience { get; init; } = LogAudience.Auto;',
    'public string? UserSummary { get; init; }',
    'public string? DebugDetails { get; init; }',
    'public string? RoleSessionId { get; init; }',
    'public string? FeatureKey { get; init; }',
    'public string? CommandId { get; init; }'
)
$structuredLogServiceRules = @(
    'entry = NormalizeAudience(entry);',
    'private static LogEntry NormalizeAudience(LogEntry entry)',
    'UserLogPrivacyPolicy.ContainsTechnicalDetails',
    'UserLogPrivacyPolicy.CreateUserSummary',
    'UserLogPrivacyPolicy.TryGetUserMessage',
    'UserSummary = userSummary,',
    'RoleSessionId = entry.RoleSessionId,',
    'FeatureKey = entry.FeatureKey,',
    'CommandId = entry.CommandId'
)
$userLogFilterRules = @(
    'UserLogPrivacyPolicy.TryGetUserMessage(entry, out var message)',
    'public static bool IsUserVisible(LogEntry entry)',
    'if (!DisplayLogEntryViewModel.IsUserVisible(entry))',
    '.Where(DisplayLogEntryViewModel.IsUserVisible)',
    '.TakeLast(MaxVisibleEntries)',
    'foreach (var entry in Entries)',
    'public string BuildVisibleExportText()',
    'public string LevelText { get; }'
)
$privacyPolicyRules = @(
    'WindowsPathRegex',
    'PosixPathRegex',
    'UrlRegex',
    'SecretRegex',
    'EmailRegex',
    'IpAddressRegex',
    'CoordinateTupleRegex',
    'BareCoordinateRegex',
    'ProcessIdentityRegex',
    'HexIdentifierRegex',
    'TimingRegex',
    'HashRegex',
    'InternalFileRegex',
    'InternalProfileNameRegex',
    'ExceptionTypeRegex',
    'MaxUserMessageLength'
)
$safeCrashRules = @(
    'WriteSafeIncidentLog("运行",',
    'WriteSafeIncidentLog("启动",',
    '反馈编号：{incidentId}',
    'support-incidents.log',
    '#if DEBUG',
    'TLBB_AGENT_INTERNAL_DIAGNOSTICS'
)
$telemetryPrivacyRules = @(
    'if (!IsDeveloperTelemetryEnabled)',
    '#if DEBUG',
    'TLBB_AGENT_INTERNAL_DIAGNOSTICS',
    'Audience = LogAudience.Diagnostic',
    'DebugDetails = ex.Message'
)
$missingStructuredLogRules = @()
$missingStructuredLogRules += @($structuredLogContractRules | Where-Object { -not $logContractSource.Contains($_) } | ForEach-Object { "Contract: $_" })
$missingStructuredLogRules += @($structuredLogServiceRules | Where-Object { -not $logServiceSource.Contains($_) } | ForEach-Object { "Service: $_" })
$missingStructuredLogRules += @($userLogFilterRules | Where-Object { -not $logViewModelSource.Contains($_) } | ForEach-Object { "UserFilter: $_" })
$missingStructuredLogRules += @($privacyPolicyRules | Where-Object { -not $logPrivacySource.Contains($_) } | ForEach-Object { "PrivacyPolicy: $_" })
$missingStructuredLogRules += @($safeCrashRules | Where-Object { -not $appCodeSource.Contains($_) } | ForEach-Object { "CrashBoundary: $_" })
$missingStructuredLogRules += @($telemetryPrivacyRules | Where-Object { -not $telemetrySource.Contains($_) } | ForEach-Object { "TelemetryBoundary: $_" })
$rawUserLogExposureRules = @(
    'Text="{Binding Window}"',
    'ToolTip="{Binding Window}"',
    'Text="{Binding Module}"',
    'ToolTip="{Binding Module}"',
    'ItemsSource="{Binding LevelOptions}"',
    'SelectedItem="{Binding MinLevel}"'
)
$visibleRawUserLogRules = @($rawUserLogExposureRules | Where-Object { $logPanelSource.Contains($_) })
$rawExportBypassRules = @('sb.AppendLine(e.ToString())', 'sb.AppendLine(entry.ToString())')
$visibleRawUserLogRules += @($rawExportBypassRules | Where-Object { $logServiceSource.Contains($_) } | ForEach-Object { "RawExport: $_" })
$rawCrashRules = @('DesktopDirectory', 'Exception.GetType()', 'Exception.Message', 'ex?.ToString()', 'ex.ToString()')
$visibleRawUserLogRules += @($rawCrashRules | Where-Object { $appCodeSource.Contains($_) } | ForEach-Object { "Crash: $_" })
[void]$results.Add([pscustomobject]@{
    page = "用户日志隐私边界"
    ok = $missingStructuredLogRules.Count -eq 0 -and $visibleRawUserLogRules.Count -eq 0
    missing = $missingStructuredLogRules
    forbiddenVisible = $visibleRawUserLogRules
    visibleNames = @()
})

$appXamlSource = Get-Content -LiteralPath $appXamlPath -Raw -Encoding UTF8
$templates = @(
    "ActivityCenterPageViewModel", "AutoCleanupPageViewModel", "MaterialProcessingPageViewModel",
    "TaskAutomationPageViewModel", "QuizAssistantPageViewModel"
)
$missingTemplates = @($templates | Where-Object { -not $appXamlSource.Contains($_) })
[void]$results.Add([pscustomobject]@{
    page = "正式页面模板"
    ok = $missingTemplates.Count -eq 0
    missing = $missingTemplates
    forbiddenVisible = @()
    visibleNames = @()
})

$ok = @($results | Where-Object { -not $_.ok }).Count -eq 0
$report = [pscustomobject]@{
    capturedAt = (Get-Date).ToString("o")
    mode = "static-contract"
    safety = [pscustomobject]@{
        startsApp = $false
        readsUserProfile = $false
        writesUserProfile = $false
        controlsGameWindow = $false
        sendsInputToGame = $false
        clicksGame = $false
        writesGameMemory = $false
    }
    pages = $results
    ok = $ok
}

$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# Agent UI P0 Smoke")
$lines.Add("")
$lines.Add("- CapturedAt: $($report.capturedAt)")
$lines.Add("- Mode: $($report.mode)")
$lines.Add("- OK: $ok")
$lines.Add("")
$lines.Add("| Page | OK | Missing | Forbidden visible |")
$lines.Add("|---|---:|---|---|")
foreach ($result in $results) {
    $lines.Add("| $($result.page) | $($result.ok) | $($result.missing -join ', ') | $($result.forbiddenVisible -join ', ') |")
}
$lines.Add("")
$lines.Add("- JSON: ``$jsonPath``")
Set-Content -LiteralPath $mdPath -Value $lines -Encoding UTF8

Write-Host "Agent UI P0 smoke"
Write-Host "OK=$ok"
Write-Host "Mode=$($report.mode)"
Write-Host "Report=$mdPath"

if (-not $ok) {
    exit 1
}
