<#
  Инструменты сборки NetZapret для Android — в одну папку, без прав
  администратора и не трогая системные (dotnet 8 окна Windows остаётся
  как есть): JDK 17, Go, Android SDK с NDK, .NET 10 с рабочей нагрузкой
  android, gomobile SagerNet.

  Запуск:  powershell -ExecutionPolicy Bypass -File android\toolchain.ps1
  Около 10 ГБ на диске (замер 27.10: .NET с нагрузкой 3,7, SDK с NDK 2,5,
  модули Go 3,1). Повторный запуск ставит только недостающее.

  Лицензии Android SDK принимает человек: без ключа -AcceptAndroidLicenses
  sdkmanager спросит сам. Ключ — для тех, кто их уже прочёл.
#>
param(
    [string] $Toolchain = (Join-Path $env:USERPROFILE '.nz-android'),
    [switch] $AcceptAndroidLicenses
)

$ErrorActionPreference = 'Stop'

# Версии — те, на которых 27.10 собрались libbox, ByeDPI и APK. Go и JDK
# требует сам форк sing-box: go.mod — go 1.26.4, build_libbox проверяет
# строку «openjdk 17»; NDK — r28, как в CI форка.
$JdkUrl = 'https://aka.ms/download-jdk/microsoft-jdk-17.0.21.0.2-windows-x64.zip'
$GoUrl = 'https://go.dev/dl/go1.26.10.windows-amd64.zip'
$CmdlineToolsUrl = 'https://dl.google.com/android/repository/commandlinetools-win-9862592_latest.zip'
$SdkPackages = @('platform-tools', 'platforms;android-36', 'build-tools;36.0.0', 'ndk;28.2.13676358')
$DotnetChannel = '10.0'
$GomobileVersion = 'v0.1.13'

$downloads = Join-Path $Toolchain 'downloads'
New-Item -ItemType Directory -Force $Toolchain, $downloads | Out-Null

function Get-File([string] $url, [string] $name) {
    $path = Join-Path $downloads $name
    if (-not (Test-Path -LiteralPath $path)) {
        & curl.exe -sSL --retry 3 -o $path $url
        if ($LASTEXITCODE -ne 0) { throw "Не скачалось: $url" }
    }
    return $path
}

# JDK 17
$jdk = Join-Path $Toolchain 'jdk17'
if (-not (Test-Path (Join-Path $jdk 'bin\java.exe'))) {
    $zip = Get-File $JdkUrl 'jdk17.zip'
    $tmp = Join-Path $Toolchain 'jdk-tmp'
    Expand-Archive -Force $zip $tmp
    Move-Item (Get-ChildItem $tmp -Directory | Select-Object -First 1).FullName $jdk
    Remove-Item -Recurse -Force $tmp
}

# Go
$go = Join-Path $Toolchain 'go'
if (-not (Test-Path (Join-Path $go 'bin\go.exe'))) {
    Expand-Archive -Force (Get-File $GoUrl 'go.zip') $Toolchain
}

# Android SDK
$sdk = Join-Path $Toolchain 'android-sdk'
$sdkmanager = Join-Path $sdk 'cmdline-tools\latest\bin\sdkmanager.bat'
if (-not (Test-Path $sdkmanager)) {
    $tmp = Join-Path $sdk 'cmdline-tools-tmp'
    Expand-Archive -Force (Get-File $CmdlineToolsUrl 'cmdline-tools.zip') $tmp
    New-Item -ItemType Directory -Force (Join-Path $sdk 'cmdline-tools') | Out-Null
    Move-Item (Join-Path $tmp 'cmdline-tools') (Join-Path $sdk 'cmdline-tools\latest')
    Remove-Item -Recurse -Force $tmp
}
$env:JAVA_HOME = $jdk
$env:ANDROID_HOME = $sdk
if ($AcceptAndroidLicenses) {
    # Канал PowerShell до .bat ответов не доносит (проверено 27.10) — через cmd.
    & cmd.exe /c "(for /l %i in (1,1,30) do @echo y) | `"$sdkmanager`" --sdk_root=`"$sdk`" --licenses" | Out-Null
} else {
    & $sdkmanager "--sdk_root=$sdk" --licenses
}
& $sdkmanager "--sdk_root=$sdk" @SdkPackages

# .NET 10 своей папкой
$dotnetRoot = Join-Path $Toolchain 'dotnet'
if (-not (Test-Path (Join-Path $dotnetRoot 'dotnet.exe'))) {
    $script = Get-File 'https://dot.net/v1/dotnet-install.ps1' 'dotnet-install.ps1'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $script -Channel $DotnetChannel -InstallDir $dotnetRoot -NoPath
}
$env:DOTNET_ROOT = $dotnetRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
# Из папки без global.json: корневой репозитория требует .NET 8, которого здесь нет.
Push-Location $Toolchain
try {
    & (Join-Path $dotnetRoot 'dotnet.exe') workload install android
} finally {
    Pop-Location
}

# gomobile из форка SagerNet — им собирается libbox (engines.ps1)
$env:GOPATH = Join-Path $Toolchain 'gopath'
$env:GOMODCACHE = Join-Path $Toolchain 'gomodcache'
$env:GOCACHE = Join-Path $Toolchain 'gocache'
$env:GOTELEMETRY = 'off'
$goExe = Join-Path $go 'bin\go.exe'
& $goExe install "github.com/sagernet/gomobile/cmd/gomobile@$GomobileVersion"
& $goExe install "github.com/sagernet/gomobile/cmd/gobind@$GomobileVersion"

# Архивы больше не нужны — место у машин сборки бывает на исходе.
Get-ChildItem $downloads -Filter *.zip | Remove-Item -Force

Write-Host "Готово: $Toolchain"
