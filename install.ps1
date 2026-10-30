# PulseBar — установка в "C:\Program Files\AI Tools\PulseBar" и автозапуск при входе в Windows.
#
#   Установка / обновление:  powershell -ExecutionPolicy Bypass -File install.ps1
#   Удаление:                powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall
#
# Сам попросит права администратора (UAC): нужно для записи в Program Files и для задачи
# Планировщика, которая запускает PulseBar с повышенными правами (температура CPU) без окна UAC.
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Uninstall) { $argList += '-Uninstall' }
    $p = Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -Wait -PassThru
    exit $p.ExitCode
}

$dest = Join-Path $env:ProgramFiles 'AI Tools\PulseBar'
$task = '\AI Tools\PulseBar'

Get-Process PulseBar -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if ($Uninstall) {
    schtasks.exe /Delete /TN $task /F 2>$null | Out-Null
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    $parent = Split-Path $dest
    if ((Test-Path $parent) -and -not (Get-ChildItem $parent -Force)) { Remove-Item $parent }
    Write-Host 'PulseBar удалён. Настройки остались в %APPDATA%\PulseBar.'
    exit 0
}

$src = Join-Path $PSScriptRoot 'publish'
if (-not (Test-Path (Join-Path $src 'PulseBar.exe'))) {
    throw "Не найдена сборка в $src. Сначала: dotnet publish -c Release -o publish"
}

New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $src '*') $dest -Recurse -Force
Copy-Item $PSCommandPath (Join-Path $dest 'install.ps1') -Force # чтобы потом удалить: install.ps1 -Uninstall

# документация рядом с программой
$readme = Join-Path $PSScriptRoot 'README.md'
if (Test-Path $readme) { Copy-Item $readme $dest -Force }
$docs = Join-Path $PSScriptRoot 'docs'
if (Test-Path $docs) {
    New-Item -ItemType Directory -Force (Join-Path $dest 'docs') | Out-Null
    Copy-Item (Join-Path $docs '*') (Join-Path $dest 'docs') -Recurse -Force
}

$exe = Join-Path $dest 'PulseBar.exe'
$p = Start-Process $exe -ArgumentList '--autostart', 'on' -Wait -PassThru
if ($p.ExitCode -ne 0) { throw 'Не удалось создать задачу автозапуска (см. %LOCALAPPDATA%\PulseBar\pulsebar.log)' }
schtasks.exe /Run /TN $task | Out-Null

Write-Host "PulseBar установлен в $dest и запущен."
Write-Host "Автозапуск: Планировщик заданий -> AI Tools -> PulseBar."
exit 0
