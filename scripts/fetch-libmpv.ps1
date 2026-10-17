# Downloads the latest libmpv build (shinchiro/mpv-winbuild-cmake) into native/.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$native = Join-Path $root 'native'
New-Item -ItemType Directory -Force $native | Out-Null

$release = Invoke-RestMethod 'https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest'
$asset = $release.assets | Where-Object name -like 'mpv-dev-x86_64-2*' | Select-Object -First 1
Write-Host "Downloading $($asset.name)..."

$tmp = Join-Path ([IO.Path]::GetTempPath()) "nation-libmpv"
New-Item -ItemType Directory -Force $tmp | Out-Null
$archive = Join-Path $tmp 'mpv-dev.7z'
Invoke-WebRequest $asset.browser_download_url -OutFile $archive

# Windows' bundled bsdtar (libarchive) can extract .7z
tar -xf $archive -C $tmp libmpv-2.dll
Move-Item -Force (Join-Path $tmp 'libmpv-2.dll') (Join-Path $native 'libmpv-2.dll')
Remove-Item -Recurse -Force $tmp
Write-Host "libmpv-2.dll -> $native"
