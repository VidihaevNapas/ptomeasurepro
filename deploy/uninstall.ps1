<#
.SYNOPSIS
    Удаление PTO Measure Pro из AutoCAD.

.DESCRIPTION
    Это НЕ инсталлятор, а эталон той единственной файловой операции, которую
    должен выполнить деинсталлятор:

        удалить %PROGRAMDATA%\Autodesk\ApplicationPlugins\PTOMeasurePro.bundle

    Больше ничего чистить не нужно: плагин не пишет в реестр Windows и не
    трогает профили AutoCAD.

    Данные пользователя (%APPDATA%\PTOMeasurePro — реестр материалов) по
    умолчанию СОХРАНЯЮТСЯ: их удаление необратимо и не нужно при переустановке.
    Для полной очистки — ключ -RemoveUserData.

.PARAMETER RemoveUserData
    Удалить также %APPDATA%\PTOMeasurePro со всеми правками реестра материалов.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File uninstall.ps1
    powershell -ExecutionPolicy Bypass -File uninstall.ps1 -RemoveUserData
#>

[CmdletBinding()]
param(
    [switch]$RemoveUserData
)

$ErrorActionPreference = "Stop"

$bundleName  = "PTOMeasurePro.bundle"
$target      = Join-Path $env:ProgramData "Autodesk\ApplicationPlugins\$bundleName"
$userDataDir = Join-Path $env:APPDATA "PTOMeasurePro"

$running = Get-Process -Name "acad" -ErrorAction SilentlyContinue
if ($running) {
    throw "AutoCAD запущен (PID: $($running.Id -join ', ')). Закрой его и повтори удаление."
}

if (Test-Path -LiteralPath $target) {
    Remove-Item -LiteralPath $target -Recurse -Force
    Write-Host "Пакет удалён: $target"
}
else {
    Write-Host "Пакет не установлен: $target"
}

if ($RemoveUserData) {
    if (Test-Path -LiteralPath $userDataDir) {
        Remove-Item -LiteralPath $userDataDir -Recurse -Force
        Write-Host "Данные пользователя удалены: $userDataDir"
    }
}
else {
    if (Test-Path -LiteralPath $userDataDir) {
        Write-Host ""
        Write-Host "Данные пользователя сохранены: $userDataDir"
        Write-Host "Для полной очистки запусти скрипт с ключом -RemoveUserData"
    }
}
