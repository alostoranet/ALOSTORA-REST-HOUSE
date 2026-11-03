# Run this script once in an elevated PowerShell window on the Windows server.
# Both ports are restricted to devices on the local subnet and Private network profile.
$rules = @(
    @{ Name = "ALOSTORA REST HOUSE - LAN TCP 8096"; Port = 8096 },
    @{ Name = "ALOSTORA REST HOUSE - Admin HTTPS TCP 8443"; Port = 8443 }
)
foreach ($item in $rules) {
    $existing = Get-NetFirewallRule -DisplayName $item.Name -ErrorAction SilentlyContinue
    if ($existing) { Remove-NetFirewallRule -DisplayName $item.Name }
    New-NetFirewallRule -DisplayName $item.Name -Direction Inbound -Action Allow -Protocol TCP -LocalPort $item.Port -Profile Private -RemoteAddress LocalSubnet | Out-Null
    Write-Host "Allowed TCP $($item.Port) from the local subnet on Private network profiles."
}
