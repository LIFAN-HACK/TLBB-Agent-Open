param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\src\TlbbAgent.Core\data\targets\monster\maps')
)

$ErrorActionPreference = 'Stop'
$baseUri = 'https://tl.changyou.com/data/monster/'
$indexUri = $baseUri + 'monster23.shtml'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Get-Utf8Html([string]$uri) {
    $client = New-Object System.Net.WebClient
    try {
        return [System.Text.Encoding]::UTF8.GetString($client.DownloadData($uri))
    }
    finally {
        $client.Dispose()
    }
}

function Decode-Text([string]$value) {
    $decoded = [System.Net.WebUtility]::HtmlDecode($value)
    return ([regex]::Replace($decoded, '<[^>]+>', '')).Trim()
}

function Safe-FilePart([string]$value) {
    $safe = $value
    foreach ($character in [System.IO.Path]::GetInvalidFileNameChars()) {
        $safe = $safe.Replace([string]$character, '_')
    }
    return $safe
}

$indexHtml = Get-Utf8Html $indexUri
$linkPattern = '<a\s+href=["''](?<file>(?:monster(?:\d+)?|index)\.shtml)["'']\s+title=["''](?<map>[^"'']+)["'']'
$links = [regex]::Matches($indexHtml, $linkPattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase) |
    ForEach-Object {
        [pscustomobject]@{
            File = $_.Groups['file'].Value
            Map = Decode-Text $_.Groups['map'].Value
        }
    } |
    Where-Object { $_.Map -and $_.Map -notmatch '^Boss$' } |
    Sort-Object File -Unique

if ($links.Count -lt 30) {
    throw "Official monster index returned too few map links: $($links.Count)."
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$written = 0
$totalEntries = 0
foreach ($link in $links) {
    $uri = $baseUri + $link.File
    $html = Get-Utf8Html $uri
    $names = [regex]::Matches(
        $html,
        '<strong[^>]*>(?<name>.*?)</strong>',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor
        [System.Text.RegularExpressions.RegexOptions]::Singleline) |
        ForEach-Object { Decode-Text $_.Groups['name'].Value } |
        Where-Object { $_ } |
        Sort-Object -Unique

    if ($names.Count -eq 0) {
        throw "No enemy names parsed from $uri."
    }

    $entries = @($names | ForEach-Object {
        [ordered]@{
            name = $_
            group = $link.Map
            allowAttack = $true
        }
    })
    $document = [ordered]@{
        schemaVersion = 1
        category = 'monster'
        group = $link.Map
        source = $uri
        sourceTitle = "Official TLBB enemy catalog - $($link.Map)"
        generatedBy = 'tools/Sync-OfficialMonsterCatalog.ps1'
        entries = $entries
    }

    $stem = [System.IO.Path]::GetFileNameWithoutExtension($link.File)
    $outputPath = Join-Path $OutputRoot ("{0}-{1}.json" -f $stem, (Safe-FilePart $link.Map))
    [System.IO.File]::WriteAllText(
        [System.IO.Path]::GetFullPath($outputPath),
        ($document | ConvertTo-Json -Depth 6),
        $utf8)
    $written++
    $totalEntries += $entries.Count
    Write-Host ("{0,-12} {1,3} {2}" -f $link.Map, $entries.Count, $outputPath)
}

Write-Host "Official monster catalog synchronized: maps=$written entries=$totalEntries"
