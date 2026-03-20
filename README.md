# MediarrGuard - Jellyfin Plugin

Automatically detects corrupt or unplayable media files in your Jellyfin library and requests replacements from **Sonarr** (TV) and **Radarr** (Movies).

## The Problem

You sit down to watch something, hit play, and get an error because the file is corrupt. You have to manually figure out what's wrong, delete the file, go to Sonarr/Radarr, find the episode/movie, and trigger a search. MediarrGuard does all of this automatically.

## How It Works

### Reactive Monitoring
When playback fails (stops below a configurable threshold), MediarrGuard:
1. Detects the failure via Jellyfin's playback events
2. **Filters out user skips** — sessions shorter than a minimum duration (default: 10s) are ignored
3. **Requires consecutive failures** — a single bad stop won't trigger action; the item must fail multiple times (default: 3) to be flagged as genuinely corrupt
4. Identifies the item as an Episode or Movie
5. Looks it up in Sonarr/Radarr
6. Deletes the corrupt file via the Sonarr/Radarr API
7. Triggers a targeted search for a replacement

### Proactive Scanning
A scheduled task (default: weekly on Sunday at 3 AM) runs `ffprobe` against every media file in your library to catch corruption before you even try to play it.

## Installation

### Manual
1. Download the latest release ZIP
2. Extract to `{Jellyfin Data}/plugins/MediarrGuard/`
3. Restart Jellyfin

### From Repository
Add this repository URL in Jellyfin Dashboard > Plugins > Repositories:
```
https://raw.githubusercontent.com/acorkran/jellyfin-plugin-mediarrguard/main/manifest.json
```

## Configuration

After installation, go to **Dashboard > Plugins > MediarrGuard** and configure:

- **Sonarr URL** and **API Key** - for TV show re-downloads
- **Radarr URL** and **API Key** - for movie re-downloads
- **Reactive Monitoring** - detect failures in real-time during playback
- **Proactive Scan** - periodic ffprobe validation of all media files
- **Failure Threshold** - percentage below which a playback stop is considered a failure (default: 3%)
- **Minimum Playback Duration** - sessions shorter than this (in seconds) are treated as user skips and ignored (default: 10)
- **Consecutive Failures Required** - number of consecutive low-progress stops before flagging as corrupt (default: 3)
- **Cooldown** - hours to wait before re-flagging the same item (default: 24)

## Requirements

- Jellyfin 10.11+
- Sonarr v3+ and/or Radarr v3+
- ffprobe (included with jellyfin-ffmpeg)

## License

GPLv3 - See [LICENSE](LICENSE)
