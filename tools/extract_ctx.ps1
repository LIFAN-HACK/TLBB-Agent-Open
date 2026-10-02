param(
    [string]$Dir = (Join-Path (Split-Path -Parent $PSScriptRoot) "tools\mem_dump"),
    [string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) "tools\flow_ctx.txt"),
    [int]$Ctx = 300
)
$gbk = [System.Text.Encoding]::GetEncoding(936)
# 合并成一个正则；用 Matches 的 .Index 拿位置，避免手算下标出错
$pattern = "dianxuan|guaguale|xuanzhuan|pingtu|quxian|data:image|image_1|'image'|卡密|unique_code|134055|wb\.|/api/|http://|\\bmp\\pic|曲线|缺口|长度|坐标|识别成功|识别失败"
$re = [System.Text.RegularExpressions.Regex]::new($pattern, 'IgnoreCase')
$seen = New-Object 'System.Collections.Generic.HashSet[string]'
$sb = New-Object System.Text.StringBuilder
foreach ($f in Get-ChildItem $Dir -Filter *.bin) {
    $s = $gbk.GetString([System.IO.File]::ReadAllBytes($f.FullName))
    foreach ($m in $re.Matches($s)) {
        $st = [Math]::Max(0, $m.Index - $Ctx)
        $len = [Math]::Min($Ctx * 2, $s.Length - $st)
        $ctx = $s.Substring($st, $len)
        $clean = [regex]::Replace($ctx, '[\x00-\x08\x0B\x0C\x0E-\x1F]+', ' ')
        $clean = [regex]::Replace($clean, ' {3,}', '  ').Trim()
        $key = $clean.Substring(0, [Math]::Min(110, $clean.Length))
        if ($seen.Add($key)) {
            [void]$sb.AppendLine("--- $($f.Name) @$($m.Value) ---")
            [void]$sb.AppendLine($clean)
            [void]$sb.AppendLine("")
        }
    }
}
[System.IO.File]::WriteAllText($Out, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "unique blocks: $($seen.Count) -> $Out"
