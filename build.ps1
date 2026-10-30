# Сборка PulseBar: публикация строки и службы в одну папку и установщик Inno Setup.
#   powershell -ExecutionPolicy Bypass -File build.ps1 [-Version 2.0.0-beta.1] [-NoInstaller]
# Результат: artifacts\publish\ (файлы программы) и artifacts\installer\PulseBar-Setup-<версия>.exe.
# Тот же сценарий запускает GitHub Actions (.github\workflows\release.yml).
param(
    [string]$Version = "2.0.0-dev",
    [switch]$NoInstaller
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$publish = Join-Path $root "artifacts\publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

foreach ($proj in "src\PulseBar\PulseBar.csproj", "src\PulseBar.Sensor\PulseBar.Sensor.csproj") {
    dotnet publish (Join-Path $root $proj) -c Release -o $publish "-p:Version=$Version" --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $proj failed" }
}
if ($NoInstaller) { return }

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found: winget install JRSoftware.InnoSetup" }
& $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$publish" (Join-Path $root "installer\PulseBar.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
Get-Item (Join-Path $root "artifacts\installer\PulseBar-Setup-$Version.exe")
