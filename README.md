# Nation

Reproductor de música para terminal, estilo lazygit. C# (.NET 8) + libmpv.

Formatos: FLAC, Opus, MP3, M4A/MP4 (solo audio), OGG, WAV, WavPack, APE — todo lo que decodifique ffmpeg.

## Setup

```powershell
./scripts/fetch-libmpv.ps1        # descarga libmpv-2.dll a native/
dotnet run --project src/Nation -- "C:\ruta\a\musica"
```

Instalador: `./scripts/build-installer.ps1` → `dist\NationSetup-<versión>.exe` (self-contained, sin admin, agrega `nation` al PATH del usuario). Requiere Inno Setup 6.

Arranca en pausa con la primera canción cargada. Sin argumentos usa la carpeta Música del usuario. `--shuffle` arranca en aleatorio. `--volume <0-100>` volumen inicial. `--offline` nunca consulta LRCLIB. `--no-api` / `--api-port <n>` controlan la API remota. `--no-media-keys` desactiva la integración con Windows. `--snapshot <archivo>` vuelca un frame como texto (debug, en mute).

## Visual

- **Color por carátula**: mismo algoritmo que el `ArtPalette.kt` de Shade (histograma de tonos ponderado por saturación). Usa la carátula embebida o `cover/folder/front.jpg|png` de la carpeta; transición suave entre canciones.
- **Espectro**: captura WASAPI loopback (portado del `SpectrumAnalyzer` de Shade). Escucha la salida completa del sistema; mientras Nation está en pausa las barras caen.
- **Letras**: incrustadas (SYLT sincronizado en MP3, `LYRICS`/`UNSYNCEDLYRICS` en FLAC/Opus, etc.) → `.lrc`/`.txt` junto al archivo → LRCLIB. Si la letra local no está sincronizada se busca una versión sincronizada en LRCLIB. Lo descargado se cachea en `%LOCALAPPDATA%\Nation\lyrics` (incluidos los "no encontrado"), así cada canción toca internet una sola vez. Se limpia la basura de Genius que meten algunos descargadores. Sincronizadas: línea activa con relleno tipo karaoke y el resto se desvanece con la distancia; sin sincronizar: scroll proporcional a la canción.
- Marquesina para títulos largos, fade-in al cambiar de canción, mini ecualizador en la cola, barra de progreso con resolución de medio carácter.
- **Efectos ciberpunk** (`f` los activa/desactiva): al cambiar de canción el título, artista y álbum se "descifran" desde glifos katakana/hex con destello blanco; ráfagas de glitch (desgarro horizontal + celdas cian/magenta) al cambiar de pista y de vez en cuando mientras suena; neón de dos tonos (color de la carátula + su complementario saturado) en espectro y barra de progreso, con cabeza incandescente y pulso de luz; un paquete de luz recorre el borde del panel; el logo brilla con los bajos; barrido de luz sobre la canción actual en la cola; la línea activa de la letra entra descifrándose y el relleno karaoke tiene borde incandescente. Todo corre solo mientras suena (en pausa sigue a 1 fps).

## Rendimiento

Medido con 323 pistas (Release, self-contained): ~0.3 % de un núcleo en pausa, ~4 % sonando (con espectro), ~44 MB de memoria privada, arranque ~1.3 s. Claves: caché de tags en `%LOCALAPPDATA%\Nation\library.json` (solo se releen archivos nuevos o modificados), la cola se pasa a mpv con un solo `loadlist`, el render solo reescribe filas que cambiaron, y el bucle duerme hasta la siguiente animación o tecla (30 fps sonando, 1 fps en reposo). La captura del espectro se apaga cuando Nation no suena.

## Teclas multimedia de Windows

Nation se publica en los controles multimedia del sistema (SMTC), así que las teclas ⏯ play/pausa, ⏭ siguiente, ⏮ anterior y ⏹ stop del teclado funcionan aunque la terminal no tenga el foco o esté minimizada. También aparece en el panel de volumen de Windows con título, artista, carátula y barra de posición (se puede adelantar desde ahí), y cualquier app que lea SMTC lo ve. Si otra app está sonando, Windows manda las teclas a la sesión activa más reciente.

## Integración con Shade (API compatible con Tauon)

Nation expone en `127.0.0.1:7814` el mismo subconjunto de la API remota de Tauon Music Box (`/api1/*`) que usa el `TauonBridge` de Shade, así que Shade lo detecta sin cambios: título/artista/álbum, carátula, posición, letra (la misma que encontró Nation, en LRC) y control de play/pausa, siguiente/anterior, seek, aleatorio y repetir. Solo escucha en loopback. Si Tauon está abierto con su API, el puerto está ocupado y el encabezado lo indica (`API ✗`); con Shade conectado muestra `⇄ shade`.

| Endpoint | |
|---|---|
| `GET /api1/status` | estado + pista actual |
| `GET /api1/pic/medium/{id}` | carátula |
| `GET /api1/lyrics/{id}` | `{ "lyrics_text": ... }` |
| `GET /api1/play` `pause` `next` `back` `shuffle` `repeat` | control |
| `GET /api1/seek/{ms}` | seek absoluto |

## Controles

| Tecla | Acción |
|---|---|
| `espacio` | play / pausa (también la tecla ⏯ del teclado, global) |
| `n` / `p` | siguiente / anterior (también ⏭ / ⏮, global) |
| `←` / `→` | seek ±5 s (`shift` ±30 s) |
| `+` / `-` | volumen |
| `m` | mute |
| `s` | aleatorio on/off (la canción actual sigue sonando) |
| `c` | saltar el cursor a la canción actual |
| `r` | repetir cola on/off |
| `l` | mostrar / ocultar letra |
| `f` | efectos ciberpunk on/off |
| `,` / `.` | desfase de letra −/+ 0.25 s |
| `j` / `k`, `↑` / `↓` | mover cursor en la cola |
| `g` / `G` | inicio / final |
| `enter` | reproducir selección |
| `q` | salir |

## Roadmap

1. ~~MVP: reproducción, cola, seek, volumen~~
2. Biblioteca (SQLite) con vistas por artista / álbum, búsqueda
3. ~~Letras sincronizadas~~ · carátula en half-blocks
4. ~~Integración con Shade (API tipo Tauon) · publicar en SMTC (teclas multimedia / overlay de Windows)~~
- Sincronización automática de letras planas (alineación con Whisper) → `.lrc` junto al archivo
5. Carátula Sixel/Kitty
