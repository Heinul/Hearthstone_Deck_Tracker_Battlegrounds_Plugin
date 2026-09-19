# BgFree installer for Hearthstone Deck Tracker.
# Usage (PowerShell):  irm https://raw.githubusercontent.com/Heinul/Hearthstone_Deck_Tracker_Battlegrounds_Plugin/main/install.ps1 | iex
$ErrorActionPreference = 'Stop'
$repo = 'Heinul/Hearthstone_Deck_Tracker_Battlegrounds_Plugin'
$appData = Join-Path $env:APPDATA 'HearthstoneDeckTracker'
$localApp = Join-Path $env:LOCALAPPDATA 'HearthstoneDeckTracker'

$exe = Get-ChildItem -Path $localApp -Directory -Filter 'app-*' -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First 1 |
    ForEach-Object { Join-Path $_.FullName 'HearthstoneDeckTracker.exe' }
if (-not $exe -or -not (Test-Path $exe)) {
    Write-Host 'Hearthstone Deck Tracker가 설치되어 있지 않습니다. https://hsreplay.net/downloads/ 에서 먼저 설치하고 한 번 실행하세요.' -ForegroundColor Red
    return
}

# 1. Close HDT (the loaded plugin copy is locked while it runs).
$proc = Get-Process HearthstoneDeckTracker -ErrorAction SilentlyContinue
if ($proc) {
    Write-Host 'HDT 종료 중...'
    $proc | ForEach-Object { $_.CloseMainWindow() | Out-Null }
    if (-not ($proc | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue)) { $proc | Stop-Process -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
}

# 2. Download the latest BgFree.dll.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'BgFree-installer' }
$asset = $release.assets | Where-Object { $_.name -eq 'BgFree.dll' } | Select-Object -First 1
if (-not $asset) { Write-Host '릴리스에 BgFree.dll이 없습니다.' -ForegroundColor Red; return }
$pluginDir = Join-Path $appData 'Plugins\BgFree'
New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile (Join-Path $pluginDir 'BgFree.dll') -Headers @{ 'User-Agent' = 'BgFree-installer' }
Write-Host "BgFree $($release.tag_name) 다운로드 완료."

# 3. Register the plugin as enabled in plugins.xml.
$pluginsXml = Join-Path $appData 'plugins.xml'
if (Test-Path $pluginsXml) {
    [xml]$doc = Get-Content $pluginsXml -Raw
} else {
    [xml]$doc = '<?xml version="1.0" encoding="utf-8"?><ArrayOfPluginSettings xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"></ArrayOfPluginSettings>'
}
$root = $doc.DocumentElement
$entry = $root.PluginSettings | Where-Object { $_.FileName -eq 'Plugins/BgFree/BgFree.dll' } | Select-Object -First 1
if (-not $entry) {
    $entry = $doc.CreateElement('PluginSettings')
    foreach ($pair in @(@('FileName', 'Plugins/BgFree/BgFree.dll'), @('IsEnabled', 'true'), @('Name', 'BgFree'))) {
        $node = $doc.CreateElement($pair[0]); $node.InnerText = $pair[1]; $entry.AppendChild($node) | Out-Null
    }
    $root.AppendChild($entry) | Out-Null
} else {
    $entry.IsEnabled = 'true'
}
$doc.Save($pluginsXml)

# 4. Turn off HDT's own Tier7 hero-picking overlay so the two do not overlap.
$configXml = Join-Path $appData 'config.xml'
if (Test-Path $configXml) {
    $cfg = Get-Content $configXml -Raw
    if ($cfg -match '<EnableBattlegroundsTier7Overlay>') {
        $cfg = $cfg -replace '<EnableBattlegroundsTier7Overlay>[^<]*</EnableBattlegroundsTier7Overlay>', '<EnableBattlegroundsTier7Overlay>false</EnableBattlegroundsTier7Overlay>'
        Set-Content -Path $configXml -Value $cfg -Encoding UTF8 -NoNewline
    }
}

# 5. Relaunch HDT.
Start-Process $exe
Write-Host '설치 완료. HDT가 다시 시작됩니다. 전장 큐를 돌리면 오버레이가 표시됩니다.' -ForegroundColor Green
