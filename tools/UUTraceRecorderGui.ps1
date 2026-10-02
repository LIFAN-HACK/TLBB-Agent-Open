Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$recorderPath = Join-Path $scriptRoot "UUTraceRecorder.ps1"
$outputRoot = Join-Path $repoRoot "artifacts\uu-observation"

$script:recorderProcess = $null
$script:runDir = ""
$script:markerFile = ""
$script:startTime = $null

function New-Button {
    param(
        [string]$Text,
        [int]$Width = 116,
        [int]$Height = 34
    )
    $button = New-Object System.Windows.Forms.Button
    $button.Text = $Text
    $button.Width = $Width
    $button.Height = $Height
    $button.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $button.BackColor = [System.Drawing.Color]::FromArgb(35, 43, 56)
    $button.ForeColor = [System.Drawing.Color]::White
    $button.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(72, 91, 120)
    $button.FlatAppearance.MouseOverBackColor = [System.Drawing.Color]::FromArgb(55, 83, 130)
    return $button
}

function Add-Log {
    param([string]$Text)
    $line = "[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $Text
    [void]$logBox.Items.Add($line)
    $logBox.TopIndex = [Math]::Max(0, $logBox.Items.Count - 1)
}

function Write-Marker {
    param(
        [string]$Event,
        [string]$Detail
    )
    if (-not $script:recorderProcess -or $script:recorderProcess.HasExited -or -not $script:markerFile) {
        Add-Log "录制未运行，标记未写入：$Detail"
        return
    }

    try {
        $safeDetail = ($Detail -replace "`r|`n", " ")
        [IO.File]::AppendAllText($script:markerFile, "$Event|$safeDetail`r`n", [Text.Encoding]::UTF8)
        Add-Log "标记：$safeDetail"
    } catch {
        Add-Log "写标记失败：$($_.Exception.Message)"
    }
}

function Start-Recorder {
    if ($script:recorderProcess -and -not $script:recorderProcess.HasExited) {
        Add-Log "录制已经在运行。"
        return
    }

    if (-not (Test-Path -LiteralPath $recorderPath)) {
        [System.Windows.Forms.MessageBox]::Show("找不到录制脚本：$recorderPath", "错误", "OK", "Error") | Out-Null
        return
    }

    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $script:runDir = Join-Path $outputRoot "uu-trace-gui-$stamp"
    New-Item -ItemType Directory -Force -Path $script:runDir | Out-Null
    $script:markerFile = Join-Path $script:runDir "markers.inbox"
    "" | Set-Content -LiteralPath $script:markerFile -Encoding UTF8

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "powershell.exe"
    $psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$recorderPath`" -RunDir `"$script:runDir`" -MarkerFile `"$script:markerFile`" -NoConsoleKeys -NoClearHost"
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    try {
        $script:recorderProcess = [System.Diagnostics.Process]::Start($psi)
        $script:startTime = Get-Date
        $pathBox.Text = $script:runDir
        $startButton.Enabled = $false
        $stopButton.Enabled = $true
        $statusLabel.Text = "状态：录制中"
        $statusLabel.ForeColor = [System.Drawing.Color]::FromArgb(100, 220, 145)
        Add-Log "开始录制：$script:runDir"
    } catch {
        Add-Log "启动录制失败：$($_.Exception.Message)"
    }
}

function Stop-Recorder {
    if (-not $script:recorderProcess -or $script:recorderProcess.HasExited) {
        Add-Log "录制未运行。"
        return
    }

    Write-Marker "stop" "gui-stop"
    $statusLabel.Text = "状态：正在停止并打包"
    $statusLabel.ForeColor = [System.Drawing.Color]::FromArgb(255, 205, 105)
    $stopButton.Enabled = $false
    Add-Log "已发送停止信号，等待 zip 生成。"
}

function Open-Output {
    if ($script:runDir -and (Test-Path -LiteralPath $script:runDir)) {
        Start-Process explorer.exe -ArgumentList "`"$script:runDir`""
    } elseif (Test-Path -LiteralPath $outputRoot) {
        Start-Process explorer.exe -ArgumentList "`"$outputRoot`""
    }
}

function Copy-OutputPath {
    if ($pathBox.Text) {
        [System.Windows.Forms.Clipboard]::SetText($pathBox.Text)
        Add-Log "已复制输出目录。"
    }
}

function Refresh-Status {
    $running = $false
    if ($script:recorderProcess -and -not $script:recorderProcess.HasExited) {
        $running = $true
    }

    if ($running) {
        $elapsed = [int]((Get-Date) - $script:startTime).TotalSeconds
        $procCount = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -in @("uu.dvm", "vv.dvm", "Game") }).Count
        $statusLabel.Text = "状态：录制中  用时 ${elapsed}s  目标进程 $procCount 个"
        $startButton.Enabled = $false
        $stopButton.Enabled = $true
    } else {
        if ($script:recorderProcess -and $script:recorderProcess.HasExited) {
            $zipPath = "$script:runDir.zip"
            if (Test-Path -LiteralPath $zipPath) {
                $statusLabel.Text = "状态：已停止，zip 已生成"
                $statusLabel.ForeColor = [System.Drawing.Color]::FromArgb(100, 220, 145)
            } else {
                $statusLabel.Text = "状态：已停止，zip 未生成"
                $statusLabel.ForeColor = [System.Drawing.Color]::FromArgb(255, 130, 130)
            }
        } else {
            $statusLabel.Text = "状态：未开始"
            $statusLabel.ForeColor = [System.Drawing.Color]::FromArgb(180, 190, 205)
        }
        $startButton.Enabled = $true
        $stopButton.Enabled = $false
    }

    if ($script:runDir) {
        $timeline = Join-Path $script:runDir "timeline.csv"
        if (Test-Path -LiteralPath $timeline) {
            try {
                $tail = Get-Content -LiteralPath $timeline -Tail 8 -ErrorAction Stop
                $timelineBox.Lines = $tail
            } catch {
            }
        }
    }
}

$form = New-Object System.Windows.Forms.Form
$form.Text = "UU 调用录制器"
$form.Size = New-Object System.Drawing.Size(760, 560)
$form.StartPosition = "CenterScreen"
$form.BackColor = [System.Drawing.Color]::FromArgb(17, 22, 31)
$form.ForeColor = [System.Drawing.Color]::White
$form.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 9)

$title = New-Object System.Windows.Forms.Label
$title.Text = "UU 调用录制器"
$title.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 15, [System.Drawing.FontStyle]::Bold)
$title.Location = New-Object System.Drawing.Point(18, 16)
$title.Size = New-Object System.Drawing.Size(300, 32)
$form.Controls.Add($title)

$hint = New-Object System.Windows.Forms.Label
$hint.Text = "先点开始录制，再操作 UU；切换功能前后点下面的标记按钮。停止后会自动打包。"
$hint.Location = New-Object System.Drawing.Point(20, 52)
$hint.Size = New-Object System.Drawing.Size(700, 22)
$hint.ForeColor = [System.Drawing.Color]::FromArgb(170, 184, 205)
$form.Controls.Add($hint)

$statusLabel = New-Object System.Windows.Forms.Label
$statusLabel.Text = "状态：未开始"
$statusLabel.Location = New-Object System.Drawing.Point(20, 84)
$statusLabel.Size = New-Object System.Drawing.Size(700, 24)
$statusLabel.ForeColor = [System.Drawing.Color]::FromArgb(180, 190, 205)
$form.Controls.Add($statusLabel)

$startButton = New-Button "开始录制" 130 38
$startButton.Location = New-Object System.Drawing.Point(20, 118)
$startButton.BackColor = [System.Drawing.Color]::FromArgb(62, 119, 232)
$startButton.Add_Click({ Start-Recorder })
$form.Controls.Add($startButton)

$stopButton = New-Button "停止并打包" 130 38
$stopButton.Location = New-Object System.Drawing.Point(162, 118)
$stopButton.Enabled = $false
$stopButton.Add_Click({ Stop-Recorder })
$form.Controls.Add($stopButton)

$openButton = New-Button "打开目录" 110 38
$openButton.Location = New-Object System.Drawing.Point(304, 118)
$openButton.Add_Click({ Open-Output })
$form.Controls.Add($openButton)

$copyButton = New-Button "复制路径" 110 38
$copyButton.Location = New-Object System.Drawing.Point(426, 118)
$copyButton.Add_Click({ Copy-OutputPath })
$form.Controls.Add($copyButton)

$pathBox = New-Object System.Windows.Forms.TextBox
$pathBox.Location = New-Object System.Drawing.Point(20, 168)
$pathBox.Size = New-Object System.Drawing.Size(700, 24)
$pathBox.ReadOnly = $true
$pathBox.BackColor = [System.Drawing.Color]::FromArgb(10, 14, 22)
$pathBox.ForeColor = [System.Drawing.Color]::FromArgb(220, 230, 245)
$pathBox.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
$form.Controls.Add($pathBox)

$group = New-Object System.Windows.Forms.GroupBox
$group.Text = "功能标记"
$group.Location = New-Object System.Drawing.Point(20, 205)
$group.Size = New-Object System.Drawing.Size(700, 130)
$group.ForeColor = [System.Drawing.Color]::White
$form.Controls.Add($group)

$markers = @(
    @("自动打怪", "auto-combat"),
    @("打死换怪", "kill-switch-target"),
    @("只打红怪", "red-only"),
    @("抢小", "grab-xiaoxiao"),
    @("抢反", "grab-fanzei"),
    @("马贼", "horse-bandit"),
    @("自动和尚", "auto-monk"),
    @("副本", "dungeon"),
    @("功能停止", "feature-stop"),
    @("普通标记", "manual-marker")
)

for ($i = 0; $i -lt $markers.Count; $i++) {
    $m = $markers[$i]
    $button = New-Button $m[0] 118 32
    $x = 16 + (($i % 5) * 132)
    $y = 28 + ([Math]::Floor($i / 5) * 42)
    $button.Location = New-Object System.Drawing.Point($x, $y)
    $label = $m[1]
    $button.Add_Click({ Write-Marker "marker" $label }.GetNewClosure())
    $group.Controls.Add($button)
}

$customLabel = New-Object System.Windows.Forms.Label
$customLabel.Text = "自定义标记"
$customLabel.Location = New-Object System.Drawing.Point(20, 350)
$customLabel.Size = New-Object System.Drawing.Size(100, 22)
$customLabel.ForeColor = [System.Drawing.Color]::FromArgb(170, 184, 205)
$form.Controls.Add($customLabel)

$customBox = New-Object System.Windows.Forms.TextBox
$customBox.Location = New-Object System.Drawing.Point(118, 347)
$customBox.Size = New-Object System.Drawing.Size(460, 24)
$customBox.BackColor = [System.Drawing.Color]::FromArgb(10, 14, 22)
$customBox.ForeColor = [System.Drawing.Color]::FromArgb(220, 230, 245)
$customBox.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
$form.Controls.Add($customBox)

$customButton = New-Button "写入" 90 28
$customButton.Location = New-Object System.Drawing.Point(590, 345)
$customButton.Add_Click({
    if ($customBox.Text.Trim()) {
        Write-Marker "custom-marker" $customBox.Text.Trim()
        $customBox.Clear()
    }
})
$form.Controls.Add($customButton)

$timelineLabel = New-Object System.Windows.Forms.Label
$timelineLabel.Text = "录制事件"
$timelineLabel.Location = New-Object System.Drawing.Point(20, 388)
$timelineLabel.Size = New-Object System.Drawing.Size(120, 22)
$timelineLabel.ForeColor = [System.Drawing.Color]::FromArgb(170, 184, 205)
$form.Controls.Add($timelineLabel)

$timelineBox = New-Object System.Windows.Forms.TextBox
$timelineBox.Location = New-Object System.Drawing.Point(20, 414)
$timelineBox.Size = New-Object System.Drawing.Size(345, 88)
$timelineBox.Multiline = $true
$timelineBox.ScrollBars = "Vertical"
$timelineBox.ReadOnly = $true
$timelineBox.BackColor = [System.Drawing.Color]::FromArgb(10, 14, 22)
$timelineBox.ForeColor = [System.Drawing.Color]::FromArgb(190, 205, 225)
$timelineBox.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
$form.Controls.Add($timelineBox)

$logLabel = New-Object System.Windows.Forms.Label
$logLabel.Text = "操作日志"
$logLabel.Location = New-Object System.Drawing.Point(385, 388)
$logLabel.Size = New-Object System.Drawing.Size(120, 22)
$logLabel.ForeColor = [System.Drawing.Color]::FromArgb(170, 184, 205)
$form.Controls.Add($logLabel)

$logBox = New-Object System.Windows.Forms.ListBox
$logBox.Location = New-Object System.Drawing.Point(385, 414)
$logBox.Size = New-Object System.Drawing.Size(335, 88)
$logBox.BackColor = [System.Drawing.Color]::FromArgb(10, 14, 22)
$logBox.ForeColor = [System.Drawing.Color]::FromArgb(220, 230, 245)
$logBox.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
$form.Controls.Add($logBox)

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 1000
$timer.Add_Tick({ Refresh-Status })
$timer.Start()

$form.Add_FormClosing({
    if ($script:recorderProcess -and -not $script:recorderProcess.HasExited) {
        $answer = [System.Windows.Forms.MessageBox]::Show("录制仍在运行，是否停止并打包？", "确认", "YesNoCancel", "Question")
        if ($answer -eq [System.Windows.Forms.DialogResult]::Yes) {
            Stop-Recorder
            Start-Sleep -Milliseconds 800
        } elseif ($answer -eq [System.Windows.Forms.DialogResult]::Cancel) {
            $_.Cancel = $true
        }
    }
})

Add-Log "工具已就绪。"
[void]$form.ShowDialog()
