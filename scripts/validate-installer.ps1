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
