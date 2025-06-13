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
- [.NET 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)
- OpenGL ES 3.0 compatible device
- FFmpeg installed

This replay viewer is not guranteed to works on platforms other than Windows and Linux. Recording on non-x86 processors (arm/risc-v) is broken because Harmony does not support these architectures.

## Installing FFmpeg
1. Grab FFmpeg binaries [here](https://www.ffmpeg.org/download.html)
  > Linux users can also install FFmpeg from package manager included in their distribution

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

  --experimental           <Flag>
    Alternatives: -experimental
    Experimental Toggle
    Toggle experimental feature

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

- .NET 8.0 SDK
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

## Planned
This is the list of stuffs that I want to changes. It can be planned features or just revamp the code.

- Live Graphs (Live PP, accuracy or difficulty)
- Split CLI system to seperate project (if you're willing to use it)
- Change the project name
