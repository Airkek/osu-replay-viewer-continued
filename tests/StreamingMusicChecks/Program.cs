using System.Runtime.InteropServices;
using System.Diagnostics;
using osu_replay_renderer_netcore.Audio;
using osu_replay_renderer_netcore.Audio.Conversion;

const int sampleRate = 48000;
const int channels = 2;
const int sourceFrames = sampleRate * 30;
const double volume = .375;
const int maximumCacheFrames = 3 * 16384;
string ffmpeg = Environment.GetEnvironmentVariable("ORV_FFMPEG") ?? "ffmpeg";
string rubberband = Environment.GetEnvironmentVariable("ORV_RUBBERBAND_FFMPEG");
string directory = Path.Combine(Path.GetTempPath(), $"orv-streaming-music-checks-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
string previousFFmpeg = FFmpegAudioTools.FFmpegExec;
FFmpegAudioTools.FFmpegExec = ffmpeg;
int passed = 0;
try
{
    string sourcePath = Path.Combine(directory, "thirty second stereo source.wav");
    WriteSource(sourcePath);
    using var reference = FFmpegAudioTools.Decode(sourcePath, volume: volume);
    Assert(reference.Samples == sourceFrames, "reference frame count changed");

    Check("one header-only decoder supplies the first PCM prefix without restarting", () =>
    {
        using (var wave = File.OpenRead(sourcePath))
        {
            var header = FloatWaveReader.ReadHeader(wave);
            Assert(header.DataBytes == sourceFrames * channels * sizeof(float) && wave.Position == 44,
                "WAVE header read consumed PCM data or changed its byte count");
        }
        using var source = new FFmpegMusicSource(sourcePath, ffmpeg, volume);
        Assert(source.Format.SampleRate == sampleRate && source.Format.Channels == channels, "source format changed");
        Assert(source.DecodedFrames == 0 && source.BufferedFrames == 0 && source.DecoderRunning && source.DecoderStarts == 1,
            "header read consumed PCM or did not retain its single decoder");
        var negative = new float[40 * channels];
        source.ReadFrames(-100, negative);
        Assert(source.DecodedFrames == 0 && source.DecoderStarts == 1 && negative.All(value => value == 0),
            "negative-only read consumed PCM or restarted the decoder");
        AssertFrames(source, reference, 0, 4096);
        Assert(source.DecoderStarts == 1, "first PCM prefix restarted the decoder after reading the WAVE header");
        Assert(source.DecodedFrames <= maximumCacheFrames && source.BufferedFrames <= maximumCacheFrames,
            "prefix read decoded or retained the full song");
    });
    Check("overlapping cache boundaries and negative or EOF ranges match full PCM", () =>
    {
        using var source = new FFmpegMusicSource(sourcePath, ffmpeg, volume);
        foreach (var range in new[] { (-7L, 24), (16377L, 47), (16394L, 59), (32763L, 701), ((long)sourceFrames - 8, 32) })
            AssertFrames(source, reference, range.Item1, range.Item2);
        Assert(source.Length == sourceFrames, "EOF did not expose the known source length");
        AssertFrames(source, reference, sourceFrames + 5L, 17);
        Assert(source.BufferedFrames <= maximumCacheFrames, "cache grew beyond its frame budget");

        using var unknownLengthSource = new FFmpegMusicSource(sourcePath, ffmpeg, volume);
        var actualTail = new float[32 * channels];
        var expectedTail = new float[actualTail.Length];
        BandLimitedPcmResampler.Read(unknownLengthSource, sourceFrames - 7.25, .75, actualTail);
        BandLimitedPcmResampler.Read(reference, sourceFrames - 7.25, .75, expectedTail);
        Assert(actualTail.AsSpan().SequenceEqual(expectedTail), "resampling did not refresh the source length at EOF");
    });
    Check("uncached WAV and MP3 seeks or block boundaries reproduce full decoding", () =>
    {
        using var source = new FFmpegMusicSource(sourcePath, ffmpeg, volume);
        foreach (long first in new[] { 12L * sampleRate + 7, 123, 20L * sampleRate + 317, 16381, 17L * sampleRate + 5, 3L * sampleRate + 129 })
        {
            AssertFrames(source, reference, first, 513);
            Assert(source.BufferedFrames <= maximumCacheFrames, "seek cache grew beyond its frame budget");
        }
        string compressedPath = Path.Combine(directory, "compressed music.mp3");
        EncodeMp3(sourcePath, compressedPath);
        using var compressedReference = FFmpegAudioTools.Decode(compressedPath, volume: volume);
        using var compressed = new FFmpegMusicSource(compressedPath, ffmpeg, volume);
        foreach (long first in new[] { 16380L, 12L * sampleRate + 7, 123, 20L * sampleRate + 317, 32765, 17L * sampleRate + 5 })
        {
            AssertFrames(compressed, compressedReference, first, 513);
            Assert(compressed.BufferedFrames <= maximumCacheFrames, "MP3 seek cache grew beyond its frame budget");
        }
        AssertFrames(compressed, compressedReference, compressedReference.Samples - 17L, 53);
        Assert(compressed.Length == compressedReference.Samples, "MP3 EOF length differs from full decoding");
    });
    Check("an early failed render requests only its used music prefix", () =>
    {
        using var source = new FFmpegMusicSource(sourcePath, ffmpeg, volume);
        MusicPlaybackSegment[] segments =
        [
            new(0, 12000, 0, .75, 1),
            new(12000, 24000, .1875, 1.25, 1)
        ];
        string actualPath = Path.Combine(directory, "early failure streamed.f32");
        string expectedPath = Path.Combine(directory, "early failure reference.f32");
        var tracked = new TrackingPcmSource(source);
        DynamicMusicRenderer.Render(tracked, segments, 24000, actualPath, ffmpeg);
        DynamicMusicRenderer.Render(reference, segments, 24000, expectedPath, ffmpeg);
        Assert(File.ReadAllBytes(actualPath).AsSpan().SequenceEqual(File.ReadAllBytes(expectedPath)),
            "streaming source changed variable-rate rendered PCM");
        Assert(new FileInfo(actualPath).Length == 24000L * channels * sizeof(float), "early render output length");
        Assert(source.DecodedFrames < sampleRate * 2 && source.BufferedFrames <= maximumCacheFrames,
            $"early failure consumed {source.DecodedFrames} frames and retained {source.BufferedFrames}");
        Assert(tracked.MaximumReadFrames <= 16384, $"resampler requested a {tracked.MaximumReadFrames}-frame PCM window");
        Console.WriteLine($"Early failure: read {source.DecodedFrames}/{sourceFrames} source frames, retained {source.BufferedFrames}");
    });
    Check("decoder cleanup releases resources and reports a failed buffered process", () =>
    {
        string disposalPath = Path.Combine(directory, "disposable source.wav");
        File.Copy(sourcePath, disposalPath);
        var source = new FFmpegMusicSource(disposalPath, ffmpeg, volume);
        try
        {
            source.ReadFrames(0, new float[512 * channels]);
            Assert(source.DecoderRunning, "prefix read did not leave a streaming decoder to dispose");
        }
        finally
        {
            source.Dispose();
        }
        Assert(!source.DecoderRunning && source.BufferedFrames == 0, "disposed decoder process or PCM cache was retained");
        Throws<ObjectDisposedException>(() => source.ReadFrames(0, new float[2]));
        File.Delete(disposalPath);
        Assert(!File.Exists(disposalPath), "disposed decoder retained the input file");

        if (!OperatingSystem.IsWindows())
        {
            string blockPath = Path.Combine(directory, "failure control PCM.f32");
            File.WriteAllBytes(blockPath, new byte[16384 * channels * sizeof(float)]);
            string decoderPath = Path.Combine(directory, "failure control decoder");
            File.WriteAllText(decoderPath, $"#!/bin/sh\nfor arg in \"$@\"; do\n  if [ \"$arg\" = 'wav' ]; then exec {ShellQuote(ffmpeg)} \"$@\"; fi\ndone\ncat {ShellQuote(blockPath)}\nprintf 'synthetic decoder failure\\n' >&2\nexit 17\n");
            File.SetUnixFileMode(decoderPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var failing = new FFmpegMusicSource(sourcePath, decoderPath, volume);
            const long failedPosition = 12L * sampleRate;
            failing.ReadFrames(failedPosition, new float[512 * channels]);
            Assert(SpinWait.SpinUntil(() => !failing.DecoderRunning, TimeSpan.FromSeconds(5)), "failure control decoder did not exit");
            Throws<InvalidDataException>(() => failing.ReadFrames(failedPosition, new float[channels]));
        }
    });
    if (string.IsNullOrWhiteSpace(rubberband))
    {
        Console.WriteLine("SKIP native tempo streaming check (set ORV_RUBBERBAND_FFMPEG)");
    }
    else
    {
        Check("pitch-preserving rendering also reads a bounded source interval", () =>
        {
            using var source = new FFmpegMusicSource(sourcePath, ffmpeg, volume);
            MusicPlaybackSegment[] segments =
            [
                new(0, 12000, 0, 1, 1.25),
                new(12000, 24000, .3125, 1, .75)
            ];
            string actualPath = Path.Combine(directory, "tempo streamed.f32");
            string expectedPath = Path.Combine(directory, "tempo reference.f32");
            DynamicMusicRenderer.Render(source, segments, 24000, actualPath, rubberband);
            DynamicMusicRenderer.Render(reference, segments, 24000, expectedPath, rubberband);
            var actualBytes = File.ReadAllBytes(actualPath);
            var expectedBytes = File.ReadAllBytes(expectedPath);
            Assert(actualBytes.Length == 24000 * channels * sizeof(float) && actualBytes.Length == expectedBytes.Length,
                "tempo render output length");
            var actual = MemoryMarshal.Cast<byte, float>(actualBytes);
            var expected = MemoryMarshal.Cast<byte, float>(expectedBytes);
            double energy = 0;
            for (int index = 0; index < actual.Length; index++)
            {
                Assert(float.IsFinite(actual[index]) && Math.Abs(actual[index] - expected[index]) < 1e-6,
                    $"streamed tempo PCM differs at value {index}");
                energy += actual[index] * actual[index];
            }
            Assert(energy / actual.Length > .0001, "tempo render unexpectedly produced silence");
            Assert(source.DecodedFrames < sampleRate * 2 && source.BufferedFrames <= maximumCacheFrames,
                $"tempo render consumed {source.DecodedFrames} frames and retained {source.BufferedFrames}");
            Console.WriteLine($"Tempo render: read {source.DecodedFrames}/{sourceFrames} source frames, retained {source.BufferedFrames}");
        });
    }
    Console.WriteLine($"Passed {passed} streaming music checks");
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

void EncodeMp3(string input, string output)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardError = true
        }
    };
    foreach (string argument in new[] { "-v", "error", "-nostdin", "-y", "-i", input, "-c:a", "libmp3lame", "-b:a", "128k", output })
        process.StartInfo.ArgumentList.Add(argument);
    process.Start();
    string diagnostics = process.StandardError.ReadToEnd();
    process.WaitForExit();
    Assert(process.ExitCode == 0, $"Synthetic MP3 encode failed: {diagnostics}");
}

static void AssertFrames(IPcmSource source, AudioBuffer reference, long first, int count)
{
    var output = new float[count * channels];
    output.AsSpan().Fill(float.NaN);
    source.ReadFrames(first, output);
    for (int frame = 0; frame < count; frame++)
    {
        long at = first + frame;
        for (int channel = 0; channel < channels; channel++)
        {
            float expected = at < 0 || at >= reference.Samples ? 0 : reference[channel, (int)at];
            Assert(output[frame * channels + channel] == expected, $"source PCM mismatch at frame {at}, channel {channel}");
        }
    }
}

static void WriteSource(string path)
{
    using var writer = new BinaryWriter(File.Create(path));
    int dataBytes = sourceFrames * channels * sizeof(float);
    writer.Write("RIFF"u8);
    writer.Write(dataBytes + 36);
    writer.Write("WAVEfmt "u8);
    writer.Write(16);
    writer.Write((ushort)3);
    writer.Write((ushort)channels);
    writer.Write(sampleRate);
    writer.Write(sampleRate * channels * sizeof(float));
    writer.Write((ushort)(channels * sizeof(float)));
    writer.Write((ushort)32);
    writer.Write("data"u8);
    writer.Write(dataBytes);
    uint noise = 12345;
    for (int frame = 0; frame < sourceFrames; frame++)
    {
        for (int channel = 0; channel < channels; channel++)
        {
            noise = unchecked(noise * 1664525 + 1013904223);
            writer.Write(((noise >> 8) / 16777216f - .5f) * .75f);
        }
    }
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}

static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class TrackingPcmSource(IPcmSource source) : IPcmSource
{
    public AudioFormat Format => source.Format;
    public long Length => source.Length;
    public int MaximumReadFrames { get; private set; }

    public void ReadFrames(long firstFrame, Span<float> output)
    {
        MaximumReadFrames = Math.Max(MaximumReadFrames, output.Length / Format.Channels);
        source.ReadFrames(firstFrame, output);
    }

    public void Dispose() => source.Dispose();
}
