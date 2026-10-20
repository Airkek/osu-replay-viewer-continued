using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using osu_replay_renderer_netcore.Audio;
using osu_replay_renderer_netcore.Audio.Conversion;
using osu_replay_renderer_netcore.CustomHosts.Record;

const int sampleRate = 44100;
const int framesPerSecond = 120;
const int videoFrames = framesPerSecond * 2;
const int outputSamples = sampleRate * 2;
string ffmpeg = Environment.GetEnvironmentVariable("ORV_FFMPEG") ?? "ffmpeg";
string directory = Path.Combine(Path.GetTempPath(), $"orv-audio-timeline-checks-{Guid.NewGuid():N}");
string previousFFmpeg = FFmpegAudioTools.FFmpegExec;
FFmpegAudioTools.FFmpegExec = ffmpeg;
Directory.CreateDirectory(directory);
int passed = 0;
try
{
    string videoPath = Path.Combine(directory, "video with spaces.mp4");
    string musicPath = Path.Combine(directory, "music.f32");
    string audioPath = Path.Combine(directory, "encoded audio.m4a");
    string outputPath = Path.Combine(directory, "muxed output.mp4");
    var source = new AudioBuffer(new AudioFormat { SampleRate = sampleRate }, sampleRate * 4);
    var segments = new List<MusicPlaybackSegment>();
    double sourceTime = 0;
    for (int frame = 0; frame < videoFrames; frame++)
    {
        double startMs = frame * 1000d / framesPerSecond;
        double durationMs = (frame + 1) * 1000d / framesPerSecond - startMs;
        double frequency = frame < 60 ? 1 : frame < 150 ? 1.5 : 2;
        segments.Add(MusicPlaybackSegment.FromClockInterval(startMs, sourceTime, durationMs, frequency, 1, sampleRate));
        sourceTime += durationMs * frequency;
    }
    double[] musicMarkers = [.25, .9, 1.6];
    foreach (double at in musicMarkers)
    {
        double sourceSeconds = at < .5 ? at : at < 1.25 ? .5 + (at - .5) * 1.5 : 1.625 + (at - 1.25) * 2;
        source[0, (int)Math.Round(sourceSeconds * sampleRate)] = .9f;
    }
    var effects = new RecordedAudioMixer(new AudioFormat { SampleRate = sampleRate });
    var impulse = new AudioBuffer(new AudioFormat { SampleRate = sampleRate }, 1);
    impulse[1, 0] = .9f;
    double[] effectMarkers = [.35, .95, 1.75];
    foreach (double at in effectMarkers)
        effects.StartVoice((long)Math.Round(at * sampleRate), impulse, 1);

    RunFFmpeg("-y", "-f", "lavfi", "-i", $"color=c=black:s=64x64:r={framesPerSecond}",
        "-frames:v", videoFrames.ToString(CultureInfo.InvariantCulture), "-c:v", "mpeg4", "-q:v", "10", videoPath);
    DynamicMusicRenderer.Render(source, segments, outputSamples, musicPath, ffmpeg);
    var audioEncoder = new ExternalAudioEncoder(audioPath, sampleRate, 2, ffmpeg);
    audioEncoder.Start();
    try
    {
        effects.Render(outputSamples, musicPath, 1, audioEncoder.Write);
    }
    finally
    {
        audioEncoder.Finish();
    }
    FFmpegAudioTools.MuxAudioVideo(videoPath, audioPath, outputPath);
    var decoded = FFmpegAudioTools.Decode(outputPath);

    Check("variable-rate music and hitsounds retain marker timing after AAC and MP4 mux", () =>
    {
        Assert(decoded.Format.SampleRate == sampleRate && decoded.Format.Channels == 2, "decoded audio format changed");
        foreach (double at in musicMarkers) AssertMarker(decoded, 0, at, "music");
        foreach (double at in effectMarkers) AssertMarker(decoded, 1, at, "hitsound");
    });
    Check("mux preserves all video frames and audio length within one AAC frame", () =>
    {
        string frameHash = RunFFmpeg("-i", outputPath, "-map", "0:v:0", "-an", "-f", "framehash", "-");
        string[] rows = frameHash.Split('\n').Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();
        Assert(rows.Length == videoFrames, $"video contains {rows.Length} frames, expected {videoFrames}");
        var timeBase = Regex.Match(frameHash, @"#tb 0:\s*(\d+)/(\d+)");
        Assert(timeBase.Success, "video time base missing");
        double tick = double.Parse(timeBase.Groups[1].Value, CultureInfo.InvariantCulture) /
            double.Parse(timeBase.Groups[2].Value, CultureInfo.InvariantCulture);
        double firstTime = double.Parse(rows[0].Split(',')[2], CultureInfo.InvariantCulture) * tick;
        string[] last = rows[^1].Split(',');
        double endTime = (double.Parse(last[2], CultureInfo.InvariantCulture) + double.Parse(last[3], CultureInfo.InvariantCulture)) * tick;
        Assert(Math.Abs(endTime - firstTime - 2) < 1e-6, $"video duration is {endTime - firstTime:R}s, expected 2s");
        Assert(decoded.Samples >= outputSamples && decoded.Samples - outputSamples < 1024,
            $"decoded audio has {decoded.Samples} samples, expected {outputSamples} plus AAC padding");
        Console.WriteLine($"Video: {rows.Length} frames, {endTime - firstTime:R}s; audio: {decoded.Samples} samples ({decoded.Samples - outputSamples} padding)");
    });
    Check("mux failure is reported and preserves the completed video", () =>
    {
        byte[] before = SHA256.HashData(File.ReadAllBytes(videoPath));
        bool failed = false;
        try
        {
            FFmpegAudioTools.MuxAudioVideo(videoPath, Path.Combine(directory, "missing audio.m4a"), Path.Combine(directory, "failed.mp4"));
        }
        catch (InvalidDataException)
        {
            failed = true;
        }
        Assert(failed, "missing mux input did not fail");
        Assert(SHA256.HashData(File.ReadAllBytes(videoPath)).AsSpan().SequenceEqual(before), "mux failure changed the source video");
    });
    Console.WriteLine($"Passed {passed} audio timeline checks");
}
finally
{
    FFmpegAudioTools.FFmpegExec = previousFFmpeg;
    Directory.Delete(directory, true);
}

void Check(string name, Action action)
{
    action();
    passed++;
    Console.WriteLine($"PASS {name}");
}

void AssertMarker(AudioBuffer buffer, int channel, double seconds, string label)
{
    int expected = (int)Math.Round(seconds * sampleRate);
    int radius = sampleRate / 16;
    int peak = expected;
    float amplitude = 0;
    for (int frame = Math.Max(0, expected - radius); frame < Math.Min(buffer.Samples, expected + radius); frame++)
    {
        float value = Math.Abs(buffer[channel, frame]);
        if (value > amplitude)
        {
            amplitude = value;
            peak = frame;
        }
    }
    double offsetMs = (peak - expected) * 1000d / sampleRate;
    Assert(amplitude > .05f, $"{label} marker at {seconds}s disappeared");
    Assert(Math.Abs(offsetMs) <= 1, $"{label} marker at {seconds}s shifted by {offsetMs:R}ms");
    Console.WriteLine($"{label} marker {seconds:F2}s: offset {offsetMs:F3}ms");
}

string RunFFmpeg(params string[] arguments)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }
    };
    foreach (string argument in new[] { "-v", "error", "-nostdin" }.Concat(arguments))
        process.StartInfo.ArgumentList.Add(argument);
    process.Start();
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    Task.WhenAll(output, error).GetAwaiter().GetResult();
    if (process.ExitCode != 0)
        throw new InvalidDataException($"FFmpeg failed with exit code {process.ExitCode}: {error.Result}");
    return output.Result;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
