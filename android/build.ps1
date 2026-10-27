<#
  NetZapret для Android одной командой: движки (если их нет), поставка
  из config\, presets\ и vendor\zapret-gui\ и APK.

  Поставку отдельно собирать не нужно: NetZapret.Android.csproj берёт файлы
  прямо из папок репозитория (AndroidAsset), поэтому правка пресета, списка
  или lua доходит до телефона этой же командой — без копий в git.

  Запуск:  powershell -ExecutionPolicy Bypass -File android\build.ps1
  Итог:    путь к APK, размер и SHA-256.
#>
param(
    [string] $Toolchain = (Join-Path $env:USERPROFILE '.nz-android'),
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',
    # Собрать без Java-части (WP3), если она временно не компилируется.
    [switch] $NoJava
)

$ErrorActionPreference = 'Stop'

$Android = $PSScriptRoot
$Project = Join-Path $Android 'NetZapret.Android\NetZapret.Android.csproj'
$Dotnet = Join-Path $Toolchain 'dotnet\dotnet.exe'
$Sdk = Join-Path $Toolchain 'android-sdk'
$Jdk = Join-Path $Toolchain 'jdk17'

foreach ($required in @($Dotnet, $Sdk, $Jdk)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Нет инструмента: $required (папка -Toolchain)." }
}

# 1. Движки — пропускаются, если уже лежат в tools\android.
& (Join-Path $Android 'engines.ps1') -Toolchain $Toolchain

# 2. APK. Из папки android\: у неё свой global.json (.NET 10) — SDK ищется
#    от текущей папки, и из корня репозитория взялся бы .NET 8 без Android.
$env:DOTNET_ROOT = Join-Path $Toolchain 'dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:JAVA_HOME = $Jdk

$arguments = @(
    'build', $Project,
    '-c', $Configuration,
    "-p:AndroidSdkDirectory=$Sdk",
    "-p:JavaSdkDirectory=$Jdk",
    '-nologo')

if ($NoJava) { $arguments += '-p:NzNoJava=true' }

Push-Location $Android
try {
    & $Dotnet @arguments
    $code = $LASTEXITCODE
}
finally {
    # Узлы MSBuild и компилятора висят после сборки ещё минут пятнадцать
    # (CLAUDE.md, «Код»): снять сразу. Из той же папки android\ — снаружи
    # dotnet взял бы чужой global.json и не нашёл бы SDK.
    & $Dotnet build-server shutdown | Out-Null
    Pop-Location
}

if ($code -ne 0) { throw "Сборка APK не прошла (код $code)." }

# 3. Что вышло.
$output = Join-Path $Android "NetZapret.Android\bin\$Configuration\net10.0-android\android-arm64"
$apk = Get-ChildItem -LiteralPath $output -Filter '*-Signed.apk' | Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $apk) { throw "APK не найден в $output." }

# 4. Последний рубеж, как проверка в конце pack.cmd: утёкший APK не отозвать.
#    Сборка уже отказывается сама (цель NzRefusePersonal в .csproj), здесь —
#    по готовому архиву, тем же образцом из .csproj (один на обоих), чтобы
#    личное не прошло и мимо цели. Читается .NET-ом: Get-Content в
#    PowerShell 5.1 прочёл бы UTF-8 без BOM как ANSI (CLAUDE.md, «Код»).
$projectXml = [xml][IO.File]::ReadAllText($Project)
$personal = @($projectXml.Project.PropertyGroup | Where-Object { $_._NzPersonalAsset } | ForEach-Object { $_._NzPersonalAsset })[0]

if (-not $personal) { throw "В .csproj нет образца личного (_NzPersonalAsset) — проверить APK нечем." }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($apk.FullName)

try {
    $leaked = @($archive.Entries | Where-Object { $_.FullName -match $personal } | ForEach-Object { $_.FullName })
}
finally {
    $archive.Dispose()
}

if ($leaked.Count -gt 0) {
    # Ни подписанного, ни неподписанного не оставлять: оба несут то же личное.
    Get-ChildItem -LiteralPath $output -Filter '*.apk' | Remove-Item -Force
    throw ("Отказ: в APK попало личное — {0}. APK удалён." -f ($leaked -join ', '))
}

$sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $apk.FullName).Hash.ToLowerInvariant()
Write-Host ''
Write-Host "APK:     $($apk.FullName)"
Write-Host ("Размер:  {0:N1} МБ" -f ($apk.Length / 1MB))
Write-Host "SHA-256: $sha"
