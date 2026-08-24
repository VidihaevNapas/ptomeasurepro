<#
.SYNOPSIS
    Установка PTO Measure Pro в AutoCAD через механизм ApplicationPlugins.

.DESCRIPTION
    Это НЕ инсталлятор, а эталон тех двух файловых операций, которые инсталлятор
    (MSI, Inno Setup, WiX — что угодно) должен выполнить:

        1. скопировать папку PTOMeasurePro.bundle
        2. в %PROGRAMDATA%\Autodesk\ApplicationPlugins

    Больше ничего не требуется: реестр Windows не правится, NETLOAD не нужен,
    AutoCAD сам сканирует ApplicationPlugins при запуске и читает PackageContents.xml.

    Скрипт годится и для ручной установки, и для обновления: старая папка
    удаляется целиком, на её место встаёт новая. Пользовательские данные лежат
    в %APPDATA%\PTOMeasurePro и не затрагиваются.

.PARAMETER BundlePath
    Путь к собранной папке PTOMeasurePro.bundle (результат цели Bundle).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install.ps1 -BundlePath ..\dist\PTOMeasurePro.bundle
#>

[CmdletBinding()]
param(
    [Parameter()]
    [string]$BundlePath = (Join-Path $PSScriptRoot "..\dist\PTOMeasurePro.bundle")
)

$ErrorActionPreference = "Stop"

$bundleName = "PTOMeasurePro.bundle"
$pluginsDir = Join-Path $env:ProgramData "Autodesk\ApplicationPlugins"
$target     = Join-Path $pluginsDir $bundleName

# --- Проверки источника ---------------------------------------------------

if (-not (Test-Path -LiteralPath $BundlePath)) {
    throw "Не найдена папка пакета: $BundlePath`nСобери её: dotnet build CadMeasure.sln -c Release -p:Platform=x64 -t:Bundle"
}

$manifest = Join-Path $BundlePath "PackageContents.xml"
if (-not (Test-Path -LiteralPath $manifest)) {
    throw "В пакете нет PackageContents.xml: $manifest"
}

$mainDll = Join-Path $BundlePath "Contents\CadMeasurePlugin.dll"
if (-not (Test-Path -LiteralPath $mainDll)) {
    throw "В пакете нет Contents\CadMeasurePlugin.dll"
}

$version = ([xml](Get-Content -LiteralPath $manifest -Encoding UTF8)).ApplicationPackage.AppVersion

# --- AutoCAD должен быть закрыт -------------------------------------------
# Иначе файлы заняты процессом и запись упадёт на середине.

$running = Get-Process -Name "acad" -ErrorAction SilentlyContinue
if ($running) {
    throw "AutoCAD запущен (PID: $($running.Id -join ', ')). Закрой его и повтори установку."
}

# --- Права на запись ------------------------------------------------------
# %PROGRAMDATA%\Autodesk\ApplicationPlugins обычно требует прав администратора.

New-Item -ItemType Directory -Path $pluginsDir -Force | Out-Null

# --- Установка / обновление -----------------------------------------------

if (Test-Path -LiteralPath $target) {
    $installed = $null
    $installedManifest = Join-Path $target "PackageContents.xml"
    if (Test-Path -LiteralPath $installedManifest) {
        $installed = ([xml](Get-Content -LiteralPath $installedManifest -Encoding UTF8)).ApplicationPackage.AppVersion
    }

    Write-Host "Обновление: установлена версия $installed, ставим $version"

    # Удаляем целиком: иначе dll от прошлой версии останутся в папке
    # и AutoCAD может загрузить их вперемешку с новыми.
    Remove-Item -LiteralPath $target -Recurse -Force
}
else {
    Write-Host "Установка версии $version"
}

Copy-Item -LiteralPath $BundlePath -Destination $target -Recurse -Force

Write-Host ""
Write-Host "Готово."
Write-Host "  Пакет:            $target"
Write-Host "  Данные пользователя: $(Join-Path $env:APPDATA 'PTOMeasurePro')  (при обновлении не трогаются)"
Write-Host ""
Write-Host "Запусти AutoCAD 2025 — плагин загрузится сам. Палитра: команда CMP."
