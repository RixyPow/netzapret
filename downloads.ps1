# Сколько раз скачан каждый выпуск — прямо сейчас, из GitHub.
#
# Для владельца: значок в README кэшируется до получаса, а число
# в «О программе» берётся один раз за запуск. Здесь — свежий ответ API
# и разбивка по версиям, которой нет ни там, ни там.
#
# Запуск: downloads.cmd (двойной щелчок). Без входа в GitHub API даёт
# 60 запросов в час с адреса — на этот скрипт хватит с запасом.
#
# Файл в UTF-8 с BOM: без него PowerShell 5.1 читает его в ANSI
# и русский текст выходит кракозябрами.

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repository = 'RixyPow/netzapret'
$headers = @{ 'User-Agent' = 'NetZapret'; 'Accept' = 'application/vnd.github+json' }

$releases = @()
try {
    # Постранично: страница — до ста выпусков.
    for ($page = 1; $page -le 20; $page++) {
        # PowerShell 5.1 отдаёт массив из ответа одним объектом —
        # без разворота через конвейер выпуски сливаются в одну строку.
        $batch = @(Invoke-RestMethod -Headers $headers `
            -Uri "https://api.github.com/repos/$repository/releases?per_page=100&page=$page" | ForEach-Object { $_ })
        $releases += $batch
        if ($batch.Count -lt 100) { break }
    }
}
catch {
    Write-Host "GitHub не ответил: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

$rows = foreach ($release in $releases) {
    $sum = 0
    foreach ($asset in $release.assets) { $sum += [long]$asset.download_count }

    [pscustomobject]@{
        'Версия'     = $release.tag_name
        'Выпущена'   = ([datetime]$release.published_at).ToLocalTime().ToString('dd.MM.yyyy')
        'Скачиваний' = $sum
    }
}

$total = ($rows | Measure-Object -Property 'Скачиваний' -Sum).Sum
if ($null -eq $total) { $total = 0 }

Write-Host ''
Write-Host "NetZapret — скачивания на $((Get-Date).ToString('dd.MM.yyyy HH:mm'))"
$rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
Write-Host "Всего: $total по $($rows.Count) выпускам"
Write-Host ''
Write-Host 'Скачивание — не установка: обновление из программы тоже засчитывается,' -ForegroundColor DarkGray
Write-Host 'и один человек может скачать несколько версий.' -ForegroundColor DarkGray
