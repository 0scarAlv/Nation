# Publishes a self-contained build (no .NET install needed) and compiles the Inno Setup installer.
# Output: Downloads\Melomano\NationSetup-<version>.exe
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$env:PATH = "C:\Program Files\dotnet;" + $env:PATH

if (-not (Test-Path (Join-Path $root 'native\libmpv-2.dll'))) {
    & (Join-Path $PSScriptRoot 'fetch-libmpv.ps1')
}

$csproj = Join-Path $root 'src\Nation\Nation.csproj'
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

$app = Join-Path $root 'dist\app'
if (Test-Path $app) { Remove-Item -Recurse -Force $app }
dotnet publish $csproj -c Release -r win-x64 --self-contained true -o $app -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found" }

& $iscc /Q "/DMyAppVersion=$version" (Join-Path $root 'installer\Nation.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

# Oscar keeps every installer in Downloads\Melomano; always drop the fresh one there.
$downloads = (New-Object -ComObject Shell.Application).NameSpace('shell:Downloads').Self.Path
$target = Join-Path $downloads 'Melomano'
New-Item -ItemType Directory -Force $target | Out-Null
$setup = Join-Path $root "dist\NationSetup-$version.exe"
Move-Item $setup $target -Force
Write-Host "Installer: $(Join-Path $target "NationSetup-$version.exe")"
