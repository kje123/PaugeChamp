# Runs PaugeChamp from source through PowerShell.
# Use this when Windows Smart App Control blocks the unsigned bin\PaugeChamp.exe.
#
#   Window:        powershell -ExecutionPolicy Bypass -WindowStyle Hidden -File PaugeChamp.ps1
#   Command line:  powershell -ExecutionPolicy Bypass -File PaugeChamp.ps1 status
#                  (same commands as PaugeChamp.exe - run with "help" for the list)
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$AppArgs)
$ErrorActionPreference = 'Stop'

if (-not ('PaugeChamp.Program' -as [type])) {
    $sources = Get-ChildItem (Join-Path $PSScriptRoot 'src') -Filter *.cs | ForEach-Object FullName
    Add-Type -Path $sources -ReferencedAssemblies System.Windows.Forms, System.Drawing, System.Core
}

[PaugeChamp.Program]::LauncherScript = $PSCommandPath
if ($null -eq $AppArgs) { $AppArgs = @() }
exit [PaugeChamp.Program]::Main([string[]]$AppArgs)
