<#
.SYNOPSIS
  Builds the release artifacts: a self-contained FastGet.exe, a portable zip and the Inno Setup installer.
.EXAMPLE
  ./build-release.ps1 -Version 1.2.1
#>
param(
    [string]$Version = "1.2.1",
    [string]$Iscc = ""
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "publish"
$app = Join-Path $out "app"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet test (Join-Path $root "tests/FastGet.Core.Tests") -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }

# Self-contained single file: users do not need the .NET runtime installed.
dotnet publish (Join-Path $root "src/FastGet.App") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:Version=$Version -o $app --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
Copy-Item (Join-Path $root "LICENSE"), (Join-Path $root "README.md") $app

$zip = Join-Path $out "FastGet-$Version-win-x64-portable.zip"
Compress-Archive -Path (Join-Path $app "*") -DestinationPath $zip

if (-not $Iscc) {
    $Iscc = @(
        (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $Iscc) { throw "Inno Setup 6 (ISCC.exe) not found" }
& $Iscc "/DAppVersion=$Version" "/DSourceDir=$app" "/O$out" (Join-Path $root "installer/FastGet.iss")
if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }

Get-ChildItem $out -File | Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
