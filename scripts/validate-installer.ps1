$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$paths = @(
    (Join-Path $root 'installer/Install-ALOSTORA-REST-HOUSE.ps1'),
    (Join-Path $root 'installer/Uninstall-ALOSTORA-REST-HOUSE.ps1')
)
foreach ($path in $paths) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Installer script missing: $path"
    }
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count -gt 0) {
        $details = ($errors | ForEach-Object { $_.Message }) -join '; '
        throw "PowerShell syntax validation failed for $path : $details"
    }
}
$project = [xml](Get-Content -LiteralPath (Join-Path $root 'src/Alostora.RestHouse/Alostora.RestHouse.csproj') -Raw)
$installerItem = $project.Project.ItemGroup.Content | Where-Object { $_.Include -like '*installer*' }
if (-not $installerItem) {
    throw 'Installer scripts are not configured for publish output.'
}
$uninstallerText = Get-Content -LiteralPath $paths[1] -Raw
if ($uninstallerText -notmatch 'ProgramData' -or $uninstallerText -notmatch 'ستبقى كما هي') {
    throw 'Uninstaller must explicitly preserve application data.'
}
Write-Host 'PASS: installer and uninstaller PowerShell syntax and publish inclusion validated.'

$issPath = Join-Path $root 'installer/ALOSTORA-REST-HOUSE.iss'
if (-not (Test-Path -LiteralPath $issPath -PathType Leaf)) {
    throw 'Single-file Inno Setup definition is missing.'
}
$iss = Get-Content -LiteralPath $issPath -Raw
if ($iss -notmatch 'OutputBaseFilename=ALOSTORA-REST-HOUSE-Setup' -or
    $iss -notmatch 'ArchitecturesAllowed=x64' -or
    $iss -notmatch 'commonappdata.*AlostoraRestHouse' -or
    $iss -notmatch 'users-modify' -or
    $iss -notmatch 'Alostora.RestHouse.exe' -or
    $iss -match 'UninstallDelete.*commonappdata') {
    throw 'Single-file installer must target Windows x64 and preserve the local application data directory.'
}
$workflow = Get-Content -LiteralPath (Join-Path $root '.github/workflows/build.yml') -Raw
if ($workflow -notmatch 'Install Inno Setup' -or
    $workflow -notmatch 'Build single-file Windows installer' -or
    $workflow -notmatch 'ALOSTORA-REST-HOUSE-Setup.exe') {
    throw 'Windows CI must compile and publish the single-file Setup.exe.'
}
Write-Host 'PASS: single-file Windows Setup.exe definition, x64 target, data preservation, and CI packaging are configured.'
