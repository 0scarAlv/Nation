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
- **Efectos ciberpunk** (`f` los activa/desactiva): al cambiar de canción el título, artista y álbum se encienden letra por letra (fantasma neón y luego destello blanco); ráfagas de glitch (desgarro horizontal + celdas cian/magenta) al cambiar de pista y de vez en cuando mientras suena; neón de dos tonos (color de la carátula + su complementario saturado) en espectro y barra de progreso, con cabeza incandescente y pulso de luz; un paquete de luz recorre el borde del panel; el logo brilla con los bajos; barrido de luz sobre la canción actual en la cola; la línea activa de la letra se revela con un borde de luz y el relleno karaoke tiene borde incandescente. Todo corre solo mientras suena (en pausa sigue a 1 fps).
- **Reacciona al ritmo**: detector de golpes (flujo espectral de los bajos con umbral adaptativo). Cada golpe ilumina el borde del reproductor, el logo (◈), el espectro y el relleno karaoke, y lanza un pulso que recorre la barra de progreso hacia atrás; los golpes fuertes disparan el glitch (máx. uno cada 3.5 s). La fila de la canción actual en la cola se ilumina con los bajos. Medidor de nivel junto al volumen, con pico que cae.
- **Visualizador** (`v` cambia): barras, espejo (crece desde el centro, bajos al medio), onda (relleno en braille, 2×4 puntos por celda) y lluvia (katakana estilo Matrix: cada columna cae más rápido y con estela más larga según su banda).
- Al arrancar, barrido diagonal tipo CRT; el estado (reproduciendo/pausa) se ilumina letra por letra al cambiar; el volumen se desliza al nuevo nivel con destello; el cursor de la cola deja una estela.

## Ecualizador

`e` abre un ecualizador gráfico de 10 bandas (31 Hz – 16 kHz, ±12 dB) en lugar de la letra. Cada banda es un filtro `equalizer` de ffmpeg dentro de mpv; los cambios se aplican en vivo con `af-command`, sin reconstruir la cadena de audio ni cortar la canción. Mientras nunca se activa no se agrega ningún filtro. Presets: Plano, Bajos, Vocal, Rock, Electrónica, Acústico, Agudos y tu curva personalizada (se conserva aunque pases por los presets). Se aplica un preamp negativo igual al mayor refuerzo para que nada sature, así que una curva con mucho refuerzo suena más baja: sube el volumen para compensar. Detrás de cada deslizador se ve el espectro en vivo de esa frecuencia. La configuración se guarda en `%LOCALAPPDATA%\Nation\eq.json`.

| Tecla (con el ecualizador abierto) | Acción |
|---|---|
| `←` / `→` | banda |
| `↑` / `↓` (`shift` ±3 dB) | ganancia ±1 dB |
| `0` | banda a 0 dB |
| `tab` / `shift+tab` | siguiente / anterior preset |
| `b` | ecualizador on/off |
| `e` / `esc` | cerrar |

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
| `e` | ecualizador |
| `v` | cambiar visualizador (barras / espejo / onda / lluvia) |
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
