param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [switch]$AutoBind,
    [switch]$Build,
    [switch]$CloseLaunched,
    [switch]$KeepLaunched
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
$appProject = Join-Path $Root "src\TlbbAgent.App\TlbbAgent.App.csproj"
$appExe = Join-Path $Root "src\TlbbAgent.App\bin\Debug\net8.0-windows\TLBB Agent.exe"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outDir = Join-Path $Root "artifacts\app-smoke-tabs\$stamp"
$jsonPath = Join-Path $outDir "app-smoke-tabs.json"
$mdPath = Join-Path $outDir "app-smoke-tabs.md"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

if ($Build) {
    $buildOutput = & $dotnet "build" $appProject "-v:minimal" 2>&1
    $buildText = ($buildOutput | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    Set-Content -LiteralPath (Join-Path $outDir "build.txt") -Value $buildText -Encoding UTF8
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed. See $(Join-Path $outDir 'build.txt')"
    }
}

function Get-MissingStaticResources {
    $files = Get-ChildItem -LiteralPath (Join-Path $Root "src\TlbbAgent.App") -Recurse -Filter *.xaml
    $defined = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($file in $files) {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($match in [regex]::Matches($text, 'x:Key="([^"]+)"')) {
            [void]$defined.Add($match.Groups[1].Value)
        }
    }

    $refs = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($file in $files) {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($match in [regex]::Matches($text, 'StaticResource\s+([^\}\s]+)')) {
            [void]$refs.Add($match.Groups[1].Value)
        }
    }

    @($refs | Where-Object { -not $defined.Contains($_) -and $_ -notmatch '^\{x:(Static|Type)' } | Sort-Object)
}

function Find-AppProcess {
    Get-Process -ErrorAction SilentlyContinue |
        Where-Object {
            ($_.MainWindowTitle -eq "TLBB Agent" -or $_.ProcessName -eq "TLBB Agent") -and
            $_.Path -like "*TlbbAgent.App*"
        } |
        Sort-Object StartTime -Descending |
        Select-Object -First 1
}

function Get-AppWindow {
    param([int]$ProcessId)
    try {
        $rootElement = [System.Windows.Automation.AutomationElement]::RootElement
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $ProcessId)
        $rootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    }
    catch {
        return $null
    }
}

function Find-AllSafe {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [System.Windows.Automation.TreeScope]$Scope,
        [System.Windows.Automation.Condition]$Condition
    )

    try {
        return $Element.FindAll($Scope, $Condition)
    }
    catch {
        return $null
    }
}

function Invoke-NamedControl {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [string]$Name
    )

    $nameCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    $matches = Find-AllSafe -Element $Window -Scope ([System.Windows.Automation.TreeScope]::Descendants) -Condition $nameCond
    if ($null -eq $matches) { return $false }
    for ($i = 0; $i -lt $matches.Count; $i++) {
        try {
            $element = $matches.Item($i)
            $type = $element.Current.ControlType.ProgrammaticName
        }
        catch {
            continue
        }
        if ($type -notlike "*ListItem*" -and $type -notlike "*Button*") {
            continue
        }

        $pattern = $null
        try {
            if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
                $pattern.Select()
                return $true
            }
            if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
                $pattern.Invoke()
                return $true
            }
        }
        catch {
            continue
        }
    }

    return $false
}

function Get-NamedButtonEnabled {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [string]$Name
    )

    $nameCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    $matches = Find-AllSafe -Element $Window -Scope ([System.Windows.Automation.TreeScope]::Descendants) -Condition $nameCond
    if ($null -eq $matches) { return $null }
    for ($i = 0; $i -lt $matches.Count; $i++) {
        try {
            $element = $matches.Item($i)
            if ($element.Current.ControlType.ProgrammaticName -like "*Button*") {
                return $element.Current.IsEnabled
            }
        }
        catch {
            continue
        }
    }
    return $null
}

function Get-InterestingText {
    param([System.Windows.Automation.AutomationElement]$Window)

    $all = Find-AllSafe -Element $Window -Scope ([System.Windows.Automation.TreeScope]::Descendants) -Condition ([System.Windows.Automation.Condition]::TrueCondition)
    if ($null -eq $all) { return @() }
    $items = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt $all.Count; $i++) {
        try {
            $name = $all.Item($i).Current.Name
        }
        catch {
            continue
        }
        if ([string]::IsNullOrWhiteSpace($name)) {
            continue
        }
        if ($name -match "挂机就绪|就绪检查|当前模式|待启动|运行中|已绑定|当前尺寸|启动会被拦截|基础打怪|目标样本|宠物出战|回点门禁") {
            if (-not $items.Contains($name)) {
                [void]$items.Add($name)
            }
        }
    }
    @($items)
}

$startedAt = Get-Date
$missingResources = @(Get-MissingStaticResources)
$safeIncidentPath = Join-Path $env:APPDATA "TLBB Agent\logs\support-incidents.log"
$safeIncidentBaselineCount = if (Test-Path -LiteralPath $safeIncidentPath) {
    @(Get-Content -LiteralPath $safeIncidentPath -Encoding UTF8).Count
} else {
    0
}

Add-Type -AssemblyName UIAutomationClient

$launchedByScript = $false
$process = Find-AppProcess
if (-not $process) {
    if (-not (Test-Path -LiteralPath $appExe)) {
        throw "App exe not found: $appExe"
    }
    Start-Process -FilePath $appExe | Out-Null
    $launchedByScript = $true
    Start-Sleep -Seconds 2
    $process = Find-AppProcess
}

if (-not $process) {
    throw "TLBB Agent 窗口未启动。"
}

$window = $null
$windowDeadline = (Get-Date).AddSeconds(20)
do {
    $window = Get-AppWindow -ProcessId $process.Id
    if (-not $window) {
        if (-not (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) {
            break
        }
        Start-Sleep -Milliseconds 250
    }
} until ($window -or (Get-Date) -ge $windowDeadline)
if (-not $window) {
    throw "找不到 TLBB Agent 主窗口，PID=$($process.Id)。"
}

$tabs = New-Object System.Collections.ArrayList
$tabNames = @("角色设置", "挂机", "活动", "生活", "任务", "答题")
if ($env:TLBB_AGENT_DEV_UI -eq "1") {
    $tabNames += "开发工具"
}
foreach ($tabName in $tabNames) {
    $clicked = Invoke-NamedControl -Window $window -Name $tabName
    Start-Sleep -Milliseconds 650
    $alive = [bool](Get-Process -Id $process.Id -ErrorAction SilentlyContinue)
    [void]$tabs.Add([pscustomobject]@{
        name = $tabName
        clicked = $clicked
        alive = $alive
    })
    if (-not $alive) {
        break
    }
}

$window = Get-AppWindow -ProcessId $process.Id
$autoBindInvoked = $false
if ($AutoBind -and $window) {
    $autoBindInvoked = Invoke-NamedControl -Window $window -Name "自动查找"
    Start-Sleep -Seconds 1
    $window = Get-AppWindow -ProcessId $process.Id
}

$startEnabled = $null
$interesting = @()
if ($window) {
    $startEnabled = Get-NamedButtonEnabled -Window $window -Name "启动挂机打怪"
    $interesting = @(Get-InterestingText -Window $window)
}

$end = Get-Date
$events = @(
    Get-WinEvent -FilterHashtable @{ LogName = "Application"; StartTime = $startedAt.AddSeconds(-2); EndTime = $end.AddSeconds(2) } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match "TLBB Agent\.exe|TlbbAgent|\.NET Runtime|Application Error|XamlParseException|InvalidOperationException" } |
        Select-Object -First 12 TimeCreated, ProviderName, Id, Message
)

$allTabsAlive = @($tabs | Where-Object { -not $_.alive }).Count -eq 0
$allTabsClicked = @($tabs | Where-Object { -not $_.clicked }).Count -eq 0
$crashEvents = @($events | Where-Object { $_.ProviderName -in @(".NET Runtime", "Application Error") })
$safeIncidentLines = if (Test-Path -LiteralPath $safeIncidentPath) {
    @(Get-Content -LiteralPath $safeIncidentPath -Encoding UTF8)
} else {
    @()
}
$newSafeIncidents = @($safeIncidentLines | Select-Object -Skip $safeIncidentBaselineCount)
$ok = $missingResources.Count -eq 0 `
      -and $allTabsAlive `
      -and $allTabsClicked `
      -and $crashEvents.Count -eq 0 `
      -and $newSafeIncidents.Count -eq 0
if ($AutoBind) {
    $ok = $ok -and ($startEnabled -eq $true)
}

$report = [pscustomobject]@{
    capturedAt = (Get-Date).ToString("o")
    mode = "app-smoke-tabs-ui-only"
    safety = [pscustomobject]@{
        controlsGameWindow = $false
        sendsInputToGame = $false
        clicksGame = $false
        writesGameMemory = $false
        injectsDll = $false
        installsLowLevelMouseHook = $true
        ignoresKeyboardInput = $true
        ignoresInjectedMouseInput = $true
        patchesFiles = $false
        mayBindWindow = [bool]$AutoBind
    }
    app = [pscustomobject]@{
        pid = $process.Id
        path = $process.Path
        launchedByScript = $launchedByScript
        aliveAtEnd = [bool](Get-Process -Id $process.Id -ErrorAction SilentlyContinue)
    }
    missingStaticResources = $missingResources
    tabs = $tabs
    autoBindInvoked = $autoBindInvoked
    startButtonEnabled = $startEnabled
    interestingText = $interesting
    crashEventCount = $crashEvents.Count
    safeIncidentCount = $newSafeIncidents.Count
    safeIncidents = $newSafeIncidents
    ok = $ok
}

$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# App Smoke Tabs")
[void]$md.AppendLine()
[void]$md.AppendLine("- CapturedAt: $($report.capturedAt)")
[void]$md.AppendLine("- OK: $ok")
[void]$md.AppendLine("- PID: $($process.Id)")
[void]$md.AppendLine("- AutoBind: $([bool]$AutoBind)")
[void]$md.AppendLine("- StartButtonEnabled: $startEnabled")
[void]$md.AppendLine("- CrashEvents: $($crashEvents.Count)")
[void]$md.AppendLine("- SafeIncidents: $($newSafeIncidents.Count)")
[void]$md.AppendLine("- MissingStaticResources: $($missingResources.Count)")
[void]$md.AppendLine()
[void]$md.AppendLine("## Tabs")
[void]$md.AppendLine()
[void]$md.AppendLine("| Tab | Clicked | Alive |")
[void]$md.AppendLine("|---|---:|---:|")
foreach ($tab in $tabs) {
    [void]$md.AppendLine("| $($tab.name) | $($tab.clicked) | $($tab.alive) |")
}
[void]$md.AppendLine()
[void]$md.AppendLine("## Interesting Text")
foreach ($item in $interesting) {
    [void]$md.AppendLine("- $item")
}
[void]$md.AppendLine()
[void]$md.AppendLine("## Evidence")
[void]$md.AppendLine("- JSON: ``$jsonPath``")
Set-Content -LiteralPath $mdPath -Value $md.ToString() -Encoding UTF8

Write-Host "App smoke tabs"
Write-Host "OK=$ok"
Write-Host "PID=$($process.Id)"
Write-Host "AutoBind=$([bool]$AutoBind)"
Write-Host "StartButtonEnabled=$startEnabled"
Write-Host "CrashEvents=$($crashEvents.Count)"
Write-Host "SafeIncidents=$($newSafeIncidents.Count)"
Write-Host "MissingStaticResources=$($missingResources.Count)"
Write-Host "Markdown=$mdPath"
Write-Host "Json=$jsonPath"

if ($launchedByScript -and ($CloseLaunched -or -not $KeepLaunched)) {
    Get-Process -Id $process.Id -ErrorAction SilentlyContinue | Stop-Process -Force
}

if ($ok) { exit 0 }
exit 1



