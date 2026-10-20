# osu! Replay Viewer Continued
_Based on osu!lazer_

Fork of [nahkd123](https://github.com/nahkd123)'s [osu-replay-viewer](https://github.com/nahkd123/osu-replay-viewer)

This replay viewer allow you to view imported replays (yes you have to import them in osu!lazer
client) without launching the actual game, and you can also render replays to video files, thanks
to FFmpeg.

This project aims to make replay viewer without modifying the official game code or write entire
thing from scratch, but uses components from the game instead. Because of this, it's much more easy
to upgrade to make UI matches with actual game

> This project somewhat implemented [this](https://github.com/ppy/osu/discussions/12986) idea (except
  we're running outside the official client)

## Features
- View downloaded replays (now with custom skins support)
- Download replays (if you can log in)
- Render replays to video file (FFmpeg required)

## Basic Usage
- [ ] TODO

## Requirements
- [.NET 10.0](https://dotnet.microsoft.com/download/dotnet/10.0)
- OpenGL ES 3.0 compatible device
- FFmpeg installed

## Installing FFmpeg
1. Grab FFmpeg binaries [here](https://www.ffmpeg.org/download.html)
  > Linux and MacOS users can also install FFmpeg from package manager included in their distribution

  > Windows users can download FFmpeg [here](https://www.gyan.dev/ffmpeg/builds/) or
    [here](https://github.com/BtbN/FFmpeg-Builds/releases)

2. Include ``ffmpeg`` in command line path
3. Confirm that it's working by running ``ffmpeg`` alone

> For the best encoding speed, you can install FFmpeg with hardware acceleration. To actually use
  hardware acceleration, see [hardware acceleration](#hardware-acceleration)

## Command Line arguments
> You can view all command line arguments by running the executable without arguments

Output of ``osu-replay-viewer --help``:
```
Usage:
  dotnet run osu-replay-viewer [options...]
  osu-replay-viewer [options...]

  --yes
    Always Yes
    Always answer yes to all prompts. Similar to 'command | yes'

  --mod-override           <<Mod Name/acronyms:AC>>
    Alternatives: -MOD
    Mod Override
    Override Mod(s). You can use 'no-mod' or 'acronyms:NM' to clear all mods

  --query                  <Keyword>
    Alternatives: -q
    Query
    Query data (Eg: find something in help index or query replays)

  --osu-mode
    Alternatives: -osu
    osu!lazer mode
    Use osu!lazer data (songs, skins, replays)

  --import-beatmap         <path/to/File.osz>
    Alternatives: -osz
    Import beatmap
    Import beatmap from file

  --list
    Alternatives: -list, -l
    List Replays
    List all local replays

  --view                   <Type (local/online/file/auto)> <Score GUID/Beatmap ID (auto)/File.osr>
    Alternatives: -view, -i
    View Replay
    Select a replay to view. This options must be always present (excluding -list options)

  --help
    Alternatives: -h
    Help Index
    View help with details

  --config                 </path/to/config.json>
    Alternatives: -c
    osu-replay-viewer config path
    Use config from file

  --record
    Alternatives: -R
    Record Mode
    Switch to record mode

  --record-output          <Output = osu-replay.mp4>
    Alternatives: -O
    Record Output
    Set record output

  --overlay-override       <true/false>
    Alternatives: -overlay
    Override Overlay Options
    Control the visiblity of player overlay

  --skin                   <Type (import/select)> <Skin name/File.osk>
    Alternatives: -skin, -s
    Select Skin
    Select a skin to use in replay

  --list-skin
    Alternatives: --list-skins, -lskins, -lskin
    List Skins
    List all available skins
```

## Build
To build this project, you need:

- .NET 10.0 SDK
- Git

Clone this repository (``git clone``), then build it with ``dotnet build -c Release`` command.

You can also build and run directly, using ``dotnet run osu-replay-viewer``

## Troubleshooting
### "No corresponding beatmap for the score could be found"
You need to import the beatmap to your current osu!lazer installation (works best with ranked maps).

## Tips
### Hardware Acceleration
To use hardware acceleration, you need:
- FFmpeg with hardware acceleration
- Compatible hardware (Intel, AMD or NVIDIA GPUs)
- Driver

Set ``video_encoder`` config option to ``h264_<qsv/amf/nvenc/videotoolbox>`` or ``hevc_<qsv/amf/nvenc/videotoolbox>`` to
enable hardware encoding.

Here is the table for hardware encoders:
| Vendor | Encoder           | Codec | Note     |
|--------|-------------------|-------|----------|
| any    | libx264           | H.264 | Uses CPU |
| Intel  | h264_qsv          | H.264 |          |
| AMD    | h264_amf          | H.264 |          |
| NVIDIA | h264_nvenc        | H.264 |          |
| Apple  | h264_videotoolbox | H.264 |          |
| any    | libx265           | HEVC  | Uses CPU |
| Intel  | hevc_qsv          | HEVC  |          |
| AMD    | hevc_amf          | HEVC  |          |
| NVIDIA | hevc_nvenc        | HEVC  |          |
| Apple  | hevc_videotoolbox | HEVC  |          |

### Encoder settings

The default recording settings remain `libx264`, preset `slow`, CRF 21. NVENC uses CQP 21 unless another rate-control mode is selected. The following `ffmpeg_options` settings are optional:

| Setting | Meaning |
|---------|---------|
| `video_rate_control` | `ConstantQuantizer`, `ConstantQuality`, `VariableBitrate`, or `ConstantBitrate`; omit or use `null` to keep the encoder defaults above |
| `video_quality` | Quality for CQP/CQ modes; the scale depends on the codec (QP/CRF/CQ/ICQ, or VideoToolbox 0–100) |
| `video_encoder_bitrate` | Target bitrate for explicitly selected VBR/CBR, for example `20M`; it does not limit CQP |
| `video_encoder_max_bitrate` | Optional maximum bitrate for CQ/VBR |
| `video_encoder_buffer_size` | Optional bitrate-control buffer size, for example `40M` |

Lower QP/CRF values generally mean higher quality; VideoToolbox uses higher values for higher quality. Values from different encoders are not equivalent. Do not combine `video_quality` with VBR/CBR, or bitrate limits with CQP. Unsupported private encoder options are rejected before recording, and presets are passed only to encoders that support them.

For example, this explicitly selects 20 Mbit/s VBR for an NVIDIA Pipe recording at 720p60 with YUV420 input. Speed and visual quality depend on the FFmpeg build, driver, and workload:

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
    "video_encoder_preset": "slow",
    "video_rate_control": "VariableBitrate",
    "video_encoder_bitrate": "20M",
    "video_encoder_max_bitrate": "30M",
    "video_encoder_buffer_size": "40M"
  },
  "output_options": {
    "pixel_format": "YUV420"
  }
}
```

### Audio and video recording

Pipe mode uses one external FFmpeg process to encode and mux video and AAC audio directly into the output file. It does not create a temporary audio file or run a final remux pass. Video and PCM audio use separate bounded inputs over loopback TCP; both endpoints are inside the same host/container, so Docker ports do not need to be published.

Pipe mode needs an FFmpeg executable with TCP input, AAC encoding, and the selected video encoder. It does not require FFmpeg shared DLLs/`.so` files or `libraries_path`; those are for Binding mode. Existing renderer and GPU driver requirements still apply. Binding encoders also mux audio directly; the separate audio encoder/remux remains a fallback for encoders without integrated audio input.

### Encoder regression checks

Run the managed queue, ownership, and option checks with .NET 10:

```sh
dotnet run --project osu-replay-viewer.Tests -c Release
```

Set `ORV_FFMPEG` to also run real software-encoder round trips and combined audio/video checks, including high-FPS startup, short clips, paths with spaces, and process failures:

```sh
ORV_FFMPEG=/path/to/ffmpeg dotnet run --project osu-replay-viewer.Tests -c Release
```

These checks use `libx264` and AAC and do not validate NVENC, QSV, AMF, or VideoToolbox hardware performance or visual equivalence.

The optional production-profile check records 120 frames at 1280×720/60 fps with YUV420, the `slow` preset, default NVENC CQP 21, and 48 kHz stereo audio. It checks decoded frame/audio lengths and a shared audio/video timing marker. Run it inside the existing NVIDIA-enabled Docker container:

```sh
ORV_FFMPEG=/usr/bin/ffmpeg ORV_HARDWARE_ENCODER=h264_nvenc ORV_HARDWARE_PRESET=p7 dotnet run --project osu-replay-viewer.Tests -c Release
```

The hardware check reports an explicit skip when `ORV_HARDWARE_ENCODER` is unset. It needs the selected encoder and GPU access in the container; no additional published ports or FFmpeg shared libraries are required for Pipe mode.

## Planned
This is the list of stuffs that I want to changes. It can be planned features or just revamp the code.

- Live Graphs (Live PP, accuracy or difficulty)
- Split CLI system to seperate project (if you're willing to use it)
- Change the project name
