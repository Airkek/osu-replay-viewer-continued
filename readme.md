# osu! Replay Viewer Continued

View osu! replays and render them to video using osu!lazer components and FFmpeg. Supports custom skins, an online leaderboard, and replay downloads through osu! API v2.

Based on [nahkd123/osu-replay-viewer](https://github.com/nahkd123/osu-replay-viewer).

## Requirements and build

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A graphics device and drivers compatible with osu!lazer
- [FFmpeg](https://www.ffmpeg.org/download.html) on `PATH` for recording, with the selected video encoder and AAC support

```sh
git clone --recurse-submodules https://github.com/Airkek/osu-replay-viewer-continued.git
cd osu-replay-viewer-continued
dotnet build -c Release
```

For an existing checkout, initialize the submodule with `git submodule update --init --recursive`.

## Usage

Run from the repository root. The matching beatmap must be imported into the selected storage before playing a replay.

View a replay using your osu!lazer beatmaps and skins:

```sh
dotnet run --project osu-replay-viewer -c Release -- --osu-mode --view file replay.osr
```

Record it to MP4:

```sh
dotnet run --project osu-replay-viewer -c Release -- --osu-mode --view file replay.osr --record --record-output replay.mp4
```

List imported replays or available skins:

```sh
dotnet run --project osu-replay-viewer -c Release -- --osu-mode --list
dotnet run --project osu-replay-viewer -c Release -- --osu-mode --list-skins
```

Useful options to append to a viewing or recording command:

| Option | Purpose |
|--------|---------|
| `--skin select "Skin name"` | Use an imported skin |
| `--skin import skin.osk --preserve` | Import a skin and keep the source archive |
| `--import-beatmap map.osz --preserve` | Import a beatmap and keep the source archive |
| `--mod-override acronyms:HD` | Override replay mods; `acronyms:NM` clears them |
| `--config config.json` | Use a different config file |
| `--yes` | Accept prompts, including overwriting output files |

Use `--view local <GUID>` for an imported replay, `--view online <score ID>` for a locally stored replay with that online ID, or `--view auto <beatmap ID>` for autoplay. `online` does not download replays.

See all options with `dotnet run --project osu-replay-viewer -c Release -- --help`.

### Storage

Without `--osu-mode`, the viewer uses its own storage. With `--osu-mode`, it reads your osu!lazer database into a temporary snapshot; explicit beatmap, skin, and replay archive imports write to the original storage. Changes made by another osu! process after the snapshot was created are not reflected until the next run. An incompatible database causes an error instead of being migrated. Configuration and logs still use the selected storage directory.

The viewer does not sign in to osu! or use saved osu!lazer login credentials.

## Recording settings

The viewer creates or updates `osu-replay-viewer-config.json` on launch. Edit it to change recording settings, or select another file with `--config`. Defaults are 1280×720 at 60 FPS, `libx264`, preset `slow`, CRF 21, and AAC at 192 kbit/s.

For example, to use NVIDIA hardware encoding:

```json
{
  "record_options": {
    "fps": 60,
    "resolution": "1280x720"
  },
  "ffmpeg_options": {
    "mode": "Pipe",
    "ffmpeg_executable": "ffmpeg",
    "video_encoder": "h264_nvenc",
    "video_encoder_preset": "p7",
    "video_rate_control": "ConstantQuantizer",
    "video_quality": 21
  },
  "output_options": {
    "pixel_format": "YUV420"
  }
}
```

Hardware encoding requires a compatible GPU, driver, and FFmpeg build. Other H.264 encoders include `h264_qsv` (Intel), `h264_amf` (AMD), and `h264_videotoolbox` (Apple). Presets and quality scales depend on the encoder.

Optional fields in `ffmpeg_options`:

| Setting | Purpose |
|---------|---------|
| `video_rate_control` | `ConstantQuantizer`, `ConstantQuality`, `VariableBitrate`, or `ConstantBitrate` |
| `video_quality` | Quality for CQP/CQ; lower generally means better quality, except VideoToolbox |
| `video_encoder_bitrate` | Target bitrate for VBR/CBR, e.g. `"20M"` |
| `video_encoder_max_bitrate`, `video_encoder_buffer_size` | Optional CQ/VBR bitrate limit and buffer size |
| `video_encoder_gop_size` | Maximum keyframe interval in frames |
| `audio_encoder_bitrate` | Audio bitrate in bits per second |

Do not combine `video_quality` with VBR/CBR, or bitrate limits with CQP.

Recording uses SingleThread execution to preserve replay accuracy. Replays with speed changes that preserve pitch require FFmpeg's `rubberband` filter; check it with `ffmpeg -hide_banner -h filter=rubberband`. Allow temporary disk space for audio processing.

The default `Pipe` mode uses the FFmpeg executable. `Binding` mode additionally requires compatible FFmpeg shared libraries configured through `libraries_path`.

## osu! API

Register an [osu! OAuth application](https://osu.ppy.sh/home/account/edit#new-oauth-application) and add its credentials to the viewer config:

```json
{
  "osu_api": {
    "client_id": 12345,
    "client_secret": "your-client-secret"
  },
  "game_settings": {
    "show_leaderboard": true
  }
}
```

Alternatively, set `OSU_API_CLIENT_ID` and `OSU_API_CLIENT_SECRET`; they override config credentials. Keep client secrets private. The default config filename is ignored by Git.

Credentials enable player profile/avatar lookup. `show_leaderboard` additionally loads the map's global top 50 before playback; it defaults to `false` and requires a leaderboard component in the active skin. Profile or leaderboard failures do not prevent playback.

Download a replay using its API v2 score ID (`id`, not `legacy_score_id`):

```sh
dotnet run --project osu-replay-viewer -c Release -- --download-replay 123456789 --replay-output replay.osr
```

Use `--osu-mode --view api 123456789` to download and play it, and append `--record --record-output replay.mp4` to record it. The beatmap must already be imported. Downloads default to `<score ID>.osr`; the output directory must exist, and `--yes` allows overwriting an existing file.

## Checks

Regression checks are standalone console projects under `osu-replay-viewer.Tests`:

```sh
dotnet run --project osu-replay-viewer.Tests/EncoderChecks -c Release
dotnet run --project osu-replay-viewer.Tests/GpuChecks -c Release
```

Use the same command with another check project's name. Set `ORV_FFMPEG=/path/to/ffmpeg` to enable FFmpeg integration checks. `DynamicMusicChecks` also uses `ORV_FFMPEG_HAS_RUBBERBAND=1` for native tempo checks; `EncoderChecks` uses `ORV_HARDWARE_ENCODER=h264_nvenc` for an optional hardware check.
