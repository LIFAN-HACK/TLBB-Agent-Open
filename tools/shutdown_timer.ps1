$ErrorActionPreference = "Stop"

function Show-Menu {
    Write-Host ""
    Write-Host "Timed Shutdown"
    Write-Host "1. Set shutdown time, like 07:00 or 23:30"
    Write-Host "2. Shutdown after N minutes"
    Write-Host "3. Cancel scheduled shutdown"
    Write-Host "4. Exit"
}

function Confirm-Action {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    $answer = Read-Host "$Message Type YES to continue"
    return $answer -eq "YES"
}

function Get-ClockTarget {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ClockText
    )

    $normalizedClockText = ($ClockText -replace '\s+', '').Replace([char]0xff1a, ':')

    if ($normalizedClockText -notmatch '^(?<hour>[01]?\d|2[0-3]):(?<minute>[0-5]\d)$') {
        throw "Invalid time. Use HH:mm, for example 07:00 or 23:30."
    }

    $hour = [int]$Matches["hour"]
    $minute = [int]$Matches["minute"]
    $now = Get-Date
    $target = $now.Date.AddHours($hour).AddMinutes($minute)

    if ($target -le $now) {
        $target = $target.AddDays(1)
    }

    return $target
}

function Schedule-Shutdown {
    param(
        [Parameter(Mandatory = $true)]
        [datetime]$TargetTime
    )

    $now = Get-Date
    $seconds = [int][Math]::Ceiling(($TargetTime - $now).TotalSeconds)

    if ($seconds -lt 0) {
        throw "Target time is already in the past."
    }

    if ($seconds -gt 315360000) {
        throw "Delay is too long. Windows shutdown.exe supports up to 315360000 seconds."
    }

    Write-Host ""
    Write-Host ("Target time : {0}" -f $TargetTime.ToString("yyyy-MM-dd HH:mm:ss"))
    Write-Host ("Delay       : {0} seconds" -f $seconds)

    if (-not (Confirm-Action "Schedule shutdown?")) {
        Write-Host "Cancelled."
        return
    }

    & shutdown.exe /s /t $seconds /c "Scheduled by shutdown_timer.ps1. Run option 3 to cancel."

    if ($LASTEXITCODE -eq 0) {
        Write-Host "Shutdown scheduled."
    }
    else {
        Write-Host "Failed to schedule shutdown. If another shutdown is already scheduled, use option 3 first."
    }
}

while ($true) {
    Show-Menu
    $choice = Read-Host "Choose 1-4"

    try {
        switch ($choice) {
            "1" {
                $clockText = Read-Host "Enter target time, for example 07:00 or 23:30"
                $target = Get-ClockTarget -ClockText $clockText
                Schedule-Shutdown -TargetTime $target
            }
            "2" {
                $minutesText = Read-Host "Enter minutes from now"
                $minutes = 0

                if (-not [int]::TryParse($minutesText, [ref]$minutes) -or $minutes -le 0) {
                    throw "Invalid minutes. Enter a positive integer."
                }

                $target = (Get-Date).AddMinutes($minutes)
                Schedule-Shutdown -TargetTime $target
            }
            "3" {
                if (Confirm-Action "Cancel scheduled shutdown?") {
                    & shutdown.exe /a

                    if ($LASTEXITCODE -eq 0) {
                        Write-Host "Scheduled shutdown cancelled."
                    }
                    else {
                        Write-Host "No scheduled shutdown was cancelled."
                    }
                }
                else {
                    Write-Host "Cancelled."
                }
            }
            "4" {
                break
            }
            default {
                Write-Host "Invalid choice."
            }
        }
    }
    catch {
        Write-Host ("Error: {0}" -f $_.Exception.Message)
    }
}
