$ErrorActionPreference = 'Stop'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"'
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments
    exit
}

$installPath = Split-Path -Parent $PSCommandPath
Get-Process -Name 'Alostora.RestHouse' -ErrorAction SilentlyContinue | Stop-Process -Force

$programs = [Environment]::GetFolderPath('Programs')
$startMenuFolder = Join-Path $programs 'ALOSTORA REST HOUSE'
$desktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
Remove-Item -LiteralPath $startMenuFolder -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $desktop 'ALOSTORA REST HOUSE.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ALOSTORA-REST-HOUSE' -Recurse -Force -ErrorAction SilentlyContinue

Write-Host 'سيتم إزالة ملفات البرنامج فقط. قاعدة البيانات والأغلفة ومكتبة الوسائط في ProgramData ستبقى كما هي.' -ForegroundColor Yellow
$confirm = Read-Host 'تأكيد إزالة البرنامج؟ (Y/N)'
if ($confirm -notmatch '^(Y|y|ن|نعم)$') {
    Write-Host 'تم إلغاء الإزالة.'
    exit
}

$quotedPath = '"' + $installPath + '"'
$command = 'timeout /t 2 /nobreak >nul & rmdir /s /q ' + $quotedPath
Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', $command) -WindowStyle Hidden
Write-Host 'بدأت إزالة ملفات البرنامج. بيانات المكتبة لم تُحذف.'
