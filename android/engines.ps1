<#
  Движки NetZapret для Android — собрать или скачать в tools\android.

  Что и откуда (android/DESIGN.md, «Сборка»):
    libbox  — sing-box extended (форк shtorm-7, тег v1.14.3-extended-2.7.3),
              gomobile bind с теми же тегами и флагами, что у build_libbox форка;
    ByeDPI  — hufrea/byedpi main@ba532298 + наша правка android\patches\byedpi-nz-auth.patch
              (SOCKS5 с паролем), clang из NDK, исполняемый PIE под именем lib*.so;
    nfqws2  — готовый android-arm64 из выпуска bol-van/zapret2 v1.0.6, сверенный
              с его sha256sum.txt и с хэшем, записанным здесь.

  Почему не в git: чужие движки со своими лицензиями и версиями, десятки
  мегабайт (как tools\* у Windows-версии). Почему не готовые сборки чужих
  людей: libbox нужен с нашими тегами, ByeDPI — с нашей правкой; повторить
  сборку должен уметь любой, кто проверяет APK.

  Уже лежащий движок пропускается; -Force — собрать заново.
  Инструменты (без прав администратора) — в -Toolchain:
  dotnet, jdk17, go, gopath\bin\gomobile.exe, android-sdk с NDK 28.2.13676358.
#>
param(
    [string] $Toolchain = (Join-Path $env:USERPROFILE '.nz-android'),
    [string] $Destination = '',
    [ValidateSet('libbox', 'byedpi', 'nfqws2')]
    [string[]] $Engines = @('libbox', 'byedpi', 'nfqws2'),
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent $PSScriptRoot
if ($Destination -eq '') { $Destination = Join-Path $Repo 'tools\android' }

$Ndk = Join-Path $Toolchain 'android-sdk\ndk\28.2.13676358'
$Downloads = Join-Path $Toolchain 'downloads'

# Версии и хэши — здесь, а не в README: сборка обязана отказаться от чужого файла.
$LibboxTag = 'v1.14.3-extended-2.7.3'
$LibboxVersion = '1.14.3-extended-2.7.3'
$ByeDpiCommit = 'ba532298de7b28cfe854aea83d061369d13ca290'
$ZapretVersion = 'v1.0.6'
$ZapretTarballSha = 'eb412aebc82cdef1f09b6aa9df45c8ff60fb88683b6663e6c4a9275d87ea2db4'
$Nfqws2Arm64Sha = '2f0d6316a4316346b87f94a17b5b406e74ab27af11813cad7fc0e7667e3eec3c'

function Write-Step([string] $text) { Write-Host "== $text" }

function Get-Sha256([string] $path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }

function Invoke-Native([string] $exe, [string[]] $arguments) {
    # В PowerShell 5.1 строка внешней программы в stderr — ErrorRecord, и при
    # Stop первое же предупреждение clang («unknown attribute») обрывало сборку.
    # Судим по коду выхода, а вывод печатаем как текст.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $exe @arguments 2>&1 | ForEach-Object { "$_" } | Out-Host }
    finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($exe)) завершился с кодом $LASTEXITCODE" }
}

function Save-Download([string] $url, [string] $path) {
    if (Test-Path -LiteralPath $path) { return }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    # curl из Windows, а не Invoke-WebRequest: тот в PowerShell 5.1 по умолчанию
    # без TLS 1.2. --ssl-no-revoke — проверка отзыва у части провайдеров
    # замерзает на 10–15 с (CLAUDE.md, «Разбор неисправностей»).
    $partial = "$path.part"
    Invoke-Native 'curl.exe' @('-fL', '--ssl-no-revoke', '--retry', '2', '-o', $partial, $url)
    Move-Item -Force -LiteralPath $partial -Destination $path
}

# --- libbox -----------------------------------------------------------------

function Build-Libbox {
    $target = Join-Path $Destination "libbox-$LibboxVersion"
    $aar = Join-Path $target 'libbox.aar'

    if ((Test-Path -LiteralPath $aar) -and -not $Force) {
        Write-Step "libbox: уже есть ($aar)"
        return
    }

    Write-Step "libbox: gomobile bind из форка sing-box extended $LibboxTag"

    $src = Join-Path $Toolchain 'src\sing-box-extended'
    if (-not (Test-Path -LiteralPath $src)) {
        Invoke-Native 'git' @('clone', '--depth', '1', '--branch', $LibboxTag, 'https://github.com/shtorm-7/sing-box-extended.git', $src)
    }

    # Тег сверяется, а не верится: рабочая копия могла остаться на другом.
    $described = (& git -C $src describe --tags --exact-match 2>$null)
    if ($described -ne $LibboxTag) { throw "В $src не тег $LibboxTag (там: $described)" }

    $env:PATH = (Join-Path $Toolchain 'go\bin') + ';' + (Join-Path $Toolchain 'gopath\bin') + ';' + (Join-Path $Toolchain 'jdk17\bin') + ';' + $env:PATH
    $env:GOPATH = Join-Path $Toolchain 'gopath'
    $env:GOMODCACHE = Join-Path $Toolchain 'gomodcache'
    $env:GOCACHE = Join-Path $Toolchain 'gocache'
    $env:GOTELEMETRY = 'off'
    $env:JAVA_HOME = Join-Path $Toolchain 'jdk17'
    $env:ANDROID_HOME = Join-Path $Toolchain 'android-sdk'
    $env:ANDROID_NDK_HOME = $Ndk

    # Теги и флаги — как у cmd/internal/build_libbox форка на этом теге (libbox.md §5).
    $tags = 'with_gvisor,with_quic,with_wireguard,with_masque,with_mtproxy,with_trusttunnel,with_call,with_sudoku,with_utls,with_naive_outbound,with_clash_api,with_usbip,with_openvpn,with_openconnect,badlinkname,tfogo_checklinkname0,with_tailscale,ts_omit_logtail,ts_omit_ssh,ts_omit_drive,ts_omit_taildrop,ts_omit_webclient,ts_omit_doctor,ts_omit_capture,ts_omit_kube,ts_omit_aws,ts_omit_synology,ts_omit_bird'
    $ldflags = "-X github.com/sagernet/sing-box/constant.Version=$LibboxVersion -X runtime.godebugDefault=multipathtcp=0,tlssha1=1 -checklinkname=0 -s -w -buildid="

    $out = Join-Path $Toolchain 'out'
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    $built = Join-Path $out 'libbox.aar'

    Push-Location $src
    try {
        Invoke-Native (Join-Path $Toolchain 'gopath\bin\gomobile.exe') @(
            'bind', '-v', '-o', $built, '-target', 'android/arm64', '-androidapi', '24',
            '-javapkg=io.nekohasekai', '-libname=box', '-trimpath', '-buildvcs=false',
            '-ldflags', $ldflags, '-tags', $tags, './experimental/libbox')
    }
    finally { Pop-Location }

    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item -Force -LiteralPath $built -Destination $aar
    $sources = Join-Path $out 'libbox-sources.jar'
    if (Test-Path -LiteralPath $sources) { Copy-Item -Force -LiteralPath $sources -Destination $target }

    Write-Host "libbox.aar sha256 $(Get-Sha256 $aar)"
}

# --- ByeDPI -----------------------------------------------------------------

function Build-ByeDpi {
    $target = Join-Path $Destination 'byedpi-ba532298-nz\arm64-v8a'
    $so = Join-Path $target 'libbyedpi.so'

    if ((Test-Path -LiteralPath $so) -and -not $Force) {
        Write-Step "ByeDPI: уже есть ($so)"
        return
    }

    Write-Step "ByeDPI: main@$($ByeDpiCommit.Substring(0, 8)) + byedpi-nz-auth.patch, NDK clang"

    $src = Join-Path $Toolchain 'src\byedpi'
    if (-not (Test-Path -LiteralPath $src)) {
        Invoke-Native 'git' @('clone', 'https://github.com/hufrea/byedpi.git', $src)
    }

    # Исходники — из коммита, а не из рабочей копии: в ней могла остаться
    # прежняя правка, и собралось бы не то, что лежит в git. autocrlf выключен,
    # иначе git отдал бы CRLF, и правка с LF не легла бы.
    $build = Join-Path $Toolchain 'out\byedpi-build'
    if (Test-Path -LiteralPath $build) { Remove-Item -Recurse -Force -LiteralPath $build }
    New-Item -ItemType Directory -Force -Path $build | Out-Null

    $archive = Join-Path $Toolchain 'out\byedpi-src.tar'
    Invoke-Native 'git' @('-C', $src, '-c', 'core.autocrlf=false', 'archive', '--format=tar', '-o', $archive, $ByeDpiCommit)
    Invoke-Native "$env:SystemRoot\System32\tar.exe" @('-xf', $archive, '-C', $build)
    Remove-Item -Force -LiteralPath $archive

    $patch = Join-Path $PSScriptRoot 'patches\byedpi-nz-auth.patch'
    Push-Location $build
    try {
        Invoke-Native 'git' @('apply', '--whitespace=nowarn', $patch)

        # Без ANDROID_APP: с ним журнал уходит в logcat, а он всегда включён —
        # имена сайтов на каждое соединение (byedpi.md §1.8). API 24 — как у приложения.
        # max-page-size=16384 — телефоны с 16-КБ страницами (Android 15+).
        $clang = Join-Path $Ndk 'toolchains\llvm\prebuilt\windows-x86_64\bin\clang.exe'
        Invoke-Native $clang @(
            '--target=aarch64-linux-android24', '-D_DEFAULT_SOURCE', '-I.', '-std=c99', '-O2',
            '-Wall', '-Wno-unused', '-Wextra', '-Wno-unused-parameter',
            '-fPIE', '-pie', '-Wl,-z,max-page-size=16384', '-s',
            '-o', 'libbyedpi.so',
            'packets.c', 'main.c', 'conev.c', 'proxy.c', 'desync.c', 'mpool.c', 'extend.c')
    }
    finally { Pop-Location }

    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item -Force -LiteralPath (Join-Path $build 'libbyedpi.so') -Destination $so
    Remove-Item -Recurse -Force -LiteralPath $build

    Write-Host "libbyedpi.so sha256 $(Get-Sha256 $so)"
}

# --- nfqws2 -----------------------------------------------------------------

function Get-Nfqws2 {
    $target = Join-Path $Destination "zapret2-$ZapretVersion\arm64-v8a"
    $so = Join-Path $target 'libnfqws2.so'

    if ((Test-Path -LiteralPath $so) -and -not $Force) {
        Write-Step "nfqws2: уже есть ($so)"
        return
    }

    Write-Step "nfqws2: выпуск bol-van/zapret2 $ZapretVersion"

    $base = "https://github.com/bol-van/zapret2/releases/download/$ZapretVersion"
    $tarball = Join-Path $Downloads "zapret2-$ZapretVersion.tar.gz"
    $sums = Join-Path $Downloads "zapret2-$ZapretVersion-sha256sum.txt"

    Save-Download "$base/zapret2-$ZapretVersion.tar.gz" $tarball
    Save-Download "$base/sha256sum.txt" $sums

    $actual = Get-Sha256 $tarball
    if ($actual -ne $ZapretTarballSha) {
        Remove-Item -Force -LiteralPath $tarball
        throw "Архив zapret2 не тот: sha256 $actual, ждали $ZapretTarballSha. Удалён — запустите снова."
    }

    $member = "zapret2-$ZapretVersion/binaries/android-arm64/nfqws2"
    $extract = Join-Path $Toolchain 'out\zapret2-extract'
    if (Test-Path -LiteralPath $extract) { Remove-Item -Recurse -Force -LiteralPath $extract }
    New-Item -ItemType Directory -Force -Path $extract | Out-Null

    Invoke-Native "$env:SystemRoot\System32\tar.exe" @('-xzf', $tarball, '-C', $extract, $member)
    $binary = Join-Path $extract ($member -replace '/', '\')

    # Две сверки: с sha256sum.txt выпуска (файл мог смениться на сервере вместе
    # с архивом) и с хэшем, записанным здесь (а sha256sum.txt — вместе с ними).
    $line = Get-Content -LiteralPath $sums | Where-Object { $_ -match ([regex]::Escape($member) + '$') } | Select-Object -First 1
    if (-not $line) { throw "В sha256sum.txt нет строки для $member" }
    $listed = ($line -split '\s+')[0].ToLowerInvariant()
    $got = Get-Sha256 $binary

    if ($got -ne $listed) { throw "nfqws2 не сходится с sha256sum.txt: $got против $listed" }
    if ($got -ne $Nfqws2Arm64Sha) { throw "nfqws2 не тот, что сверен при переходе на $ZapretVersion`: $got против $Nfqws2Arm64Sha" }

    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item -Force -LiteralPath $binary -Destination $so
    Remove-Item -Recurse -Force -LiteralPath $extract

    Write-Host "libnfqws2.so sha256 $got"
}

foreach ($engine in $Engines) {
    switch ($engine) {
        'libbox' { Build-Libbox }
        'byedpi' { Build-ByeDpi }
        'nfqws2' { Get-Nfqws2 }
    }
}
