param(
    [string]$InstallPath = (Join-Path $env:ProgramFiles 'ALOSTORA REST HOUSE')
)

$ErrorActionPreference = 'Stop'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -InstallPath "' + $InstallPath + '"'
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments
    exit
}

$sourcePath = Split-Path -Parent $PSCommandPath
$sourceExe = Join-Path $sourcePath 'Alostora.RestHouse.exe'
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
    throw 'لم يتم العثور على Alostora.RestHouse.exe. فك ضغط الحزمة كاملة ثم شغّل Install-ALOSTORA-REST-HOUSE.ps1 من داخل المجلد نفسه.'
}

$process = Get-Process -Name 'Alostora.RestHouse' -ErrorAction SilentlyContinue
foreach ($item in $process) {
    if ($item.MainWindowHandle -ne 0) { [void]$item.CloseMainWindow() }
}
Start-Sleep -Seconds 2
Get-Process -Name 'Alostora.RestHouse' -ErrorAction SilentlyContinue | Stop-Process -Force

New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
Get-ChildItem -LiteralPath $sourcePath -Force | Where-Object {
    $_.Name -notin @('Install-ALOSTORA-REST-HOUSE.ps1', 'Uninstall-ALOSTORA-REST-HOUSE.ps1')
} | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $InstallPath -Recurse -Force
}
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $InstallPath 'Install-ALOSTORA-REST-HOUSE.ps1') -Force
$uninstallerSource = Join-Path $sourcePath 'Uninstall-ALOSTORA-REST-HOUSE.ps1'
if (-not (Test-Path -LiteralPath $uninstallerSource -PathType Leaf)) { throw 'لم يتم العثور على ملف إزالة البرنامج داخل الحزمة.' }
Copy-Item -LiteralPath $uninstallerSource -Destination (Join-Path $InstallPath 'Uninstall-ALOSTORA-REST-HOUSE.ps1') -Force

$installedExe = Join-Path $InstallPath 'Alostora.RestHouse.exe'
if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf)) {
    throw 'فشل نسخ ملف تشغيل البرنامج إلى مجلد التثبيت.'
}

$programs = [Environment]::GetFolderPath('Programs')
$startMenuFolder = Join-Path $programs 'ALOSTORA REST HOUSE'
$desktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
New-Item -ItemType Directory -Path $startMenuFolder -Force | Out-Null

$shell = New-Object -ComObject WScript.Shell
$startShortcut = $shell.CreateShortcut((Join-Path $startMenuFolder 'ALOSTORA REST HOUSE.lnk'))
$startShortcut.TargetPath = $installedExe
$startShortcut.WorkingDirectory = $InstallPath
$startShortcut.Description = 'إدارة وتشغيل مكتبة استراحة الأسطورة على الشبكة المحلية'
$startShortcut.Save()

$desktopShortcut = $shell.CreateShortcut((Join-Path $desktop 'ALOSTORA REST HOUSE.lnk'))
$desktopShortcut.TargetPath = $installedExe
$desktopShortcut.WorkingDirectory = $InstallPath
$desktopShortcut.Description = 'ALOSTORA REST HOUSE'
$desktopShortcut.Save()

$uninstallKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ALOSTORA-REST-HOUSE'
New-Item -Path $uninstallKey -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'ALOSTORA REST HOUSE' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value '1.0.0' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name Publisher -Value 'ALOSTORA' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $InstallPath -PropertyType String -Force | Out-Null
$uninstaller = Join-Path $InstallPath 'Uninstall-ALOSTORA-REST-HOUSE.ps1'
$uninstallCommand = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $uninstaller + '"'
New-ItemProperty -Path $uninstallKey -Name UninstallString -Value $uninstallCommand -PropertyType String -Force | Out-Null

Write-Host ''
Write-Host 'تم تثبيت ALOSTORA REST HOUSE بنجاح.' -ForegroundColor Green
Write-Host ('مجلد البرنامج: ' + $InstallPath)
Write-Host 'تم إنشاء اختصار في قائمة ابدأ وسطح المكتب.'
Write-Host 'بيانات المكتبة في ProgramData لم يتم حذفها أو نقلها.'
$launch = Read-Host 'هل تريد تشغيل البرنامج الآن؟ (Y/N)'
if ($launch -match '^(Y|y|ن|نعم)$') { Start-Process -FilePath $installedExe -WorkingDirectory $InstallPath }
