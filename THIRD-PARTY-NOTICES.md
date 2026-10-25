# Componentes de terceros

Nation se distribuye bajo la [GPL-3.0](LICENSE). Incluye o usa estos componentes:

| Componente | Licencia | Fuente |
|---|---|---|
| **libmpv** (`libmpv-2.dll`, build de shinchiro/mpv-winbuild-cmake, compilado con la opción `gpl`) | GPL-2.0-or-later (el build incluye componentes GPL-3.0) | https://github.com/mpv-player/mpv · https://github.com/shinchiro/mpv-winbuild-cmake |
| **yt-dlp** (build oficial `yt-dlp_win.zip`, carpeta `yt-dlp`) | Unlicense (el build incluye Python y dependencias con sus propias licencias, en `_internal`) | https://github.com/yt-dlp/yt-dlp |
| FFmpeg (dentro de libmpv) | GPL (según la configuración del build) | https://ffmpeg.org |
| TagLibSharp 2.3.0 | LGPL-2.1 | https://github.com/mono/taglib-sharp |
| NAudio.Wasapi 2.3.0 | MIT | https://github.com/naudio/NAudio |
| Microsoft.Data.Sqlite.Core 8.0.11 | MIT | https://github.com/dotnet/efcore |
| SQLitePCLRaw 2.1.10 (con el `winsqlite3.dll` que ya trae Windows) | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| .NET 8 runtime (incluido en el instalador) | MIT | https://github.com/dotnet/runtime |

El código fuente de libmpv y de sus dependencias corresponde a las versiones publicadas en los repositorios anteriores; `scripts/fetch-libmpv.ps1` descarga el build exacto que se empaqueta.

Las letras se consultan opcionalmente en [LRCLIB](https://lrclib.net) (desactivable con `--offline`).
