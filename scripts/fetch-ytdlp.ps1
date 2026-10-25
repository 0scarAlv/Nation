# Downloads the latest official yt-dlp Windows build (unpacked, yt-dlp_win.zip) into native/yt-dlp.
# Always fetched fresh at build time: YouTube changes break old yt-dlp versions.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$target = Join-Path (Join-Path $root 'native') 'yt-dlp'

# The tag of the latest release, from where GitHub's "latest" link redirects (same as YtDlp.cs).
$request = [Net.WebRequest]::Create('https://github.com/yt-dlp/yt-dlp/releases/latest')
$request.AllowAutoRedirect = $false
$response = $request.GetResponse()
$version = ($response.Headers['Location'] -split '/')[-1]
$response.Close()
Write-Host "Downloading yt-dlp $version..."

$tmp = Join-Path ([IO.Path]::GetTempPath()) "nation-ytdlp"
if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
New-Item -ItemType Directory -Force $tmp | Out-Null
$zip = Join-Path $tmp 'yt-dlp_win.zip'
Invoke-WebRequest "https://github.com/yt-dlp/yt-dlp/releases/download/$version/yt-dlp_win.zip" -OutFile $zip
Expand-Archive $zip -DestinationPath (Join-Path $tmp 'x')

# The zip may hold the files at its root or inside one folder.
$exe = Get-ChildItem (Join-Path $tmp 'x') -Recurse -Filter 'yt-dlp.exe' | Select-Object -First 1
if (-not $exe) { throw "yt-dlp_win.zip no trae yt-dlp.exe" }
if (Test-Path $target) { Remove-Item -Recurse -Force $target }
Move-Item $exe.DirectoryName $target
[IO.File]::WriteAllText((Join-Path $target 'nation-version.txt'), $version)
Remove-Item -Recurse -Force $tmp
Write-Host "yt-dlp $version -> $target"
