# KrakenEliteScreenManager

A native, **sudo-free Linux** tool for the **NZXT Kraken Elite** LCDs (`1e71:300c`, `1e71:3012`) — a small
stand-in for NZXT CAM, which doesn't run on Linux. It talks to the cooler directly over USB
(HidSharp + LibUsbDotNet); no CAM, no liquidctl at runtime.

> **Scope:** this targets known compatible Kraken Elite firmware/protocol variants,
> currently **300c** (Kraken 2023 Elite) and **3012** (Kraken Elite RGB 2024).
> Other Kraken models use different screens/protocols and are **not** supported.

It has two parts:
- a **GUI config editor** — pick what the screen shows, and
- a **background systemd user service** — keeps that running across reboots, with or without the GUI.

## Six modes
- **GIF Loop** — a GIF you pick (local file or GIPHY search) is uploaded once and the cooler's
  firmware **loops it natively**, full-speed and smooth. Just the GIF, nothing drawn over it.
- **Dashboard** — a live system dashboard **streamed in real time** (raw full frames, smooth, no
  flicker): clock + date, two **KDE-style rings** for CPU and GPU **load**, the CPU/GPU **temps**
  under each ring, and coolant temp — all color-graded green→yellow→red.
- **GIF + Dashboard** — the live dashboard composited **on top of your animating GIF**, with a
  legibility option (frosted chips / dim / vignette + a dimming slider).
- **Web Page** — stream **any URL or your own local HTML page** to the LCD. A local page is served
  same-origin so it can `fetch('/data.json')` for live sensors (below). YouTube links auto-embed and
  loop, muted. If `yt-dlp` + `ffmpeg` are installed, YouTube links use direct media streaming
  automatically (most reliable); otherwise embed mode is used and may fail on some systems.
- **Video** — loop a **local video file** (mp4/mkv/webm/…), decoded by **FFmpeg** (any codec).
- **Stock Coolant** — the built-in NZXT liquid-temperature screen.

The GUI shows a **live preview** of what's on the LCD (the service writes the current frame to
`preview.png`, which the editor displays).

Live streaming uses NZXT CAM's own LCD protocol (asset mode `0x09`, full-frame BGR888), reverse-
engineered from a USB capture — so the 640×640 screen updates smoothly, which liquidctl can't do on
this device. A **frame-rate cap** (Display settings; default *Max*) limits CPU/USB load if you want.

### Live sensor feed
In Dashboard / GIF+Dashboard / local Web Page modes, sensors are served at
`http://localhost:9234/data.json` for your own pages:
`coolant`, `cpu`, `gpu` (°C), `cpuLoad`, `gpuLoad`, `ram`, `gpuMem` (%), `netRx`, `netTx` (KB/s).

## Requirements
- **.NET 10 SDK** — <https://dotnet.microsoft.com/download/dotnet/10.0>
- A **systemd**-based Linux (the service uses `systemctl --user`).
- Optional: `nvidia-smi` for NVIDIA GPU stats; AMD GPUs are read from `/sys` automatically.
- Optional, for **Video mode**: `ffmpeg` (e.g. `sudo dnf install ffmpeg`).
- Optional, for **Web Page mode YouTube fallback**: `yt-dlp` (used automatically if browser embed fails).
- Optional, for **Web Page mode with YouTube/video sites**: a system `chrome`/`chromium`
  install with media codecs. (Playwright's bundled Chromium is enough for Dashboard rendering,
  but may fail on YouTube playback.)
- Optional, for **GIPHY search** only: a free GIPHY API key. Get one at
  <https://developers.giphy.com> and export it before launching the GUI:
  ```bash
  export GIPHY_API_KEY=your_key_here
  ```
  Without it, GIPHY search is disabled but **Local GIF…** still works.

## Install (fresh machine)
```bash
git clone <repo> kraken-elite-screen-manager
cd kraken-elite-screen-manager
./scripts/install.sh
```
`install.sh` does everything: checks for .NET 10 → `dotnet publish -c Release` → downloads the
Playwright Chromium used for Dashboard/local page rendering (not guaranteed to include YouTube
media codecs) → installs the udev rule for sudo-free USB (prompts for sudo) →
installs + enables + starts the user service. If the Kraken was already plugged in and you aren't in
the `wheel` group, replug its USB once so permissions apply.

> **⚠️ Don't move the folder after installing.** The systemd unit's `ExecStart` is an absolute path to
> the build output. If you rename or move the project directory, the service can't find its binary —
> just **re-run `./scripts/install.sh`** (or hit **Apply & Start** in the GUI) to regenerate the unit
> with the new path.

## Use it
```bash
dotnet run -c Release      # opens the GUI config editor
```
Pick **GIF Loop** (search GIPHY or **Local GIF…**, click one), **Dashboard**, or **Stock Coolant**,
then **✓ Apply & Start**. **■ Stop service** turns it off (screen reverts to coolant).

The service **auto-starts at login** and reapplies your last choice — you don't need to open the GUI
again. Open it only to change something.

## Configuration
Everything lives in one XDG directory:
```
~/.config/kraken-elite-screen-manager/
├── config.json   # your settings
└── bg.gif        # the chosen GIF (GIF Loop mode)
```

`config.json` is the serialized settings:
```json
{ "Mode": "Dashboard", "RefreshSeconds": 5, "Brightness": 80, "Rotation": 270 }
```

| Field | Type | Default | Meaning |
|-------|------|---------|---------|
| `Mode` | string | `"Coolant"` | `GifLoop` \| `Dashboard` \| `GifDashboard` \| `WebPage` \| `Video` \| `Coolant` |
| `Brightness` | int | `80` | LCD brightness % |
| `Rotation` | int | `270` | Panel-mount rotation; content is pre-rotated by this. Change if the image is sideways. |
| `MaxFps` | int | `0` | Streaming frame-rate cap; `0` = uncapped (max) |
| `WebUrl` | string | `""` | Web Page mode: a URL, YouTube link, or local `.html` path |
| `VideoFile` | string | `""` | Video mode: path to a local video file |
| `OverlayStyle` | string | `"Chips"` | GIF+Dashboard legibility: `Chips` \| `Dim` \| `Vignette` \| `None` |
| `OverlayDim` | int | `45` | Legibility intensity (0–100) |

A missing or malformed file falls back to all-defaults (so a fresh machine just shows the coolant
screen until you pick a mode).

## How it works
- The **GUI never touches the LCD directly.** It only writes `config.json` (+ copies the GIF) and
  then (re)starts the service. That's why you can close the GUI and the screen keeps running.
- The **service** (`KrakenEliteScreenManager.dll service`, started by systemd) reads `config.json`
  and drives the screen, re-reading it whenever it (re)starts. `Restart=always` brings it back if it
  crashes or the USB hiccups; on a clean stop it restores the upright coolant screen.
- The **systemd unit is generated, not shipped** — `SystemdManager` writes
  `~/.config/systemd/user/kraken-elite-screen-manager.service` with `ExecStart` pointing at the
  installed binary, then `enable`s it (start at login). It's a per-user unit (`systemctl --user`),
  reproducible on any machine via the GUI's Apply or `install.sh`.

Manage the service manually if you like:
```bash
systemctl --user status  kraken-elite-screen-manager
systemctl --user restart kraken-elite-screen-manager
journalctl  --user -u    kraken-elite-screen-manager -f
```

## Hardware sensors (auto-detected)
- **Coolant**: the Kraken's own `kraken2023elite` hwmon temp.
- **CPU temp**: `k10temp` (AMD) or `coretemp` (Intel).
- **CPU load**: `/proc/stat`.
- **GPU temp + load**: `nvidia-smi` if present, otherwise an AMD card via `/sys`
  (`gpu_busy_percent` + hwmon temp). Missing sensors just show `--`.

## Develop
Open `KrakenEliteScreenManager.slnx` in Rider/Visual Studio, or from the CLI:
```bash
dotnet build          # build
dotnet run            # run the GUI (Debug)
dotnet run -- service # run the daemon in the foreground (for debugging)
```
The IDE folder (`.idea/`) and build output (`bin/`, `obj/`) are git-ignored; Rider regenerates its
solution metadata from the `.slnx` on first open.

## Notes / limits
- **GIF Loop** uses the firmware's native GIF asset path (uploaded once, looped on-device).
- **Dashboard** and **GIF + Dashboard** stream full 640×640 BGR888 frames over the bulk endpoint
  using CAM's `0x09` asset mode — smooth and flicker-free, paced by the render/capture rate.
- The older bucket-based GIF upload path (what liquidctl uses) only sustained ~4 fps before the
  firmware wedged; the streaming path replaced it for live content.

## License
MIT — see [LICENSE](LICENSE).
