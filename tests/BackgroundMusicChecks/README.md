# Background music correctness checks

The checks compile the production music sources directly. They require .NET 10 when building and an FFmpeg executable with `asendcmd` and `rubberband` when running. Set `ORV_FFMPEG` to its full path if it is absent from `PATH`.

```sh
dotnet publish tests/BackgroundMusicChecks/BackgroundMusicChecks.csproj -c Release -r linux-x64 --self-contained true -o /tmp/music-checks -m:1 --disable-build-servers
ORV_FFMPEG=/usr/bin/ffmpeg /tmp/music-checks/BackgroundMusicChecks
```

The executable reports 25 checks:

- Eligibility checks for finite, nonempty, non-unity-tempo runs
- 18 exact PCM comparisons against `DynamicMusicRenderer`: 44100/48000 Hz, three independent frequency/tempo combinations, negative lead-in, half-sample and nonbinary fractional offsets, source EOF, and output silence
- A 2000-frame timeline with one coalescing pending horizon
- Tempo, pitch, seek and gap rejection followed by the foreground dynamic renderer
- Final validation of the complete observed timeline
- Worker failure with an untouched foreground source
- PCM consumer failure with bounded worker completion, the original failure reason and native-process cleanup
- Cancellation of an idle native worker, source disposal and an independent foreground decoder

The negative-lead-in cases publish enough intervals to reach actual source audio before checking background progress. A first interval consisting entirely of silence does not need to read its source. An early worker failure reports the completion/fallback reason.

The optimization keeps replay and timeline collection on the original thread. A worker owns an independent decoder and one persistent constant-parameter Rubber Band process. Only immutable source horizons cross a capacity-one channel. Final acceptance checks the complete timeline; frequency/tempo changes, gaps, seeks or failures use the existing foreground renderer. Samples, mixing, AAC and mux retain their existing order.

The host exposes `BackgroundMusicWorkerStarts`, `BackgroundMusicAccepted` and `BackgroundMusicFallbacks` counters and prints `Background music worker: started`, `accepted` or `fallback (...)`. A performance comparison must verify acceptance and use normal full recording with Release, Pipe and `h264_nvenc`.
