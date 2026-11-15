<#
.SYNOPSIS
  Publishes Whatts as a single exe, then builds the setup wizard with Inno Setup 6.
.EXAMPLE
  .\build.ps1                # -> installer\Output\Whatts-Setup-<version>.exe
  .\build.ps1 -NoInstaller   # -> publish\Whatts.exe only
#>
param([switch]$NoInstaller)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = (Select-Xml -Path "$root\Whatts.csproj" -XPath '//Version').Node.InnerText

Remove-Item "$root\publish" -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$root\Whatts.csproj" -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:DebugType=none -o "$root\publish"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
if ($NoInstaller) { Get-Item "$root\publish\Whatts.exe"; return }

$iscc = @(
  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

& $iscc /Q "/DAppVersion=$version" "$root\installer\Whatts.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed" }
Get-Item "$root\installer\Output\Whatts-Setup-$version.exe"
