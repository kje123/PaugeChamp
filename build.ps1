# Builds PaugeChamp.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
# No Visual Studio or SDK needed.  Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc (.NET Framework 4.x is required)" }

$root = $PSScriptRoot
$out = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object FullName
& $csc /nologo /target:winexe /platform:x64 /optimize+ /warn:4 `
    "/out:$(Join-Path $out 'PaugeChamp.exe')" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    $sources
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

Write-Host "Built $(Join-Path $out 'PaugeChamp.exe')"
