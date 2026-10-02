# Creates a Start menu shortcut ("PaugeChamp") that launches the app without a console window.
#   powershell -ExecutionPolicy Bypass -File install-shortcut.ps1 [-Startup]
# -Startup also starts it minimized to the tray when you sign in.
param([switch]$Startup)

$script = Join-Path $PSScriptRoot 'PaugeChamp.ps1'
$shell = New-Object -ComObject WScript.Shell

function New-AppShortcut($path, $extraArgs) {
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $lnk.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`" $extraArgs"
    $lnk.WorkingDirectory = $PSScriptRoot
    $lnk.WindowStyle = 7   # minimized, so the console doesn't flash
    $lnk.Description = 'PaugeChamp'
    $lnk.Save()
    Write-Host "Created $path"
}

New-AppShortcut (Join-Path ([Environment]::GetFolderPath('Programs')) 'PaugeChamp.lnk') ''
if ($Startup) {
    New-AppShortcut (Join-Path ([Environment]::GetFolderPath('Startup')) 'PaugeChamp.lnk') '--minimized'
}
