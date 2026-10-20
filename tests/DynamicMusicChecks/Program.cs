using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using osu_replay_renderer_netcore.Audio;
using osu_replay_renderer_netcore.Audio.Conversion;

int passed = 0;
string directory = Path.Combine(Path.GetTempPath(), $"orv-music-checks-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
try
{
    Check("unity rate preserves original stereo PCM exactly", () =>
    {
        var source = Source(10000, 2);
        for (int frame = 0; frame < source.Samples; frame++)
        {
            source[0, frame] = MathF.Sin(frame * .1f);
            source[1, frame] = MathF.Cos(frame * .17f);
        }
        var output = Render(source, [Segment(0, source.Samples)], source.Samples);
        Assert(output.AsSpan().SequenceEqual(source.Data), "unity PCM changed");
    });
    Check("44100Hz NM capture at 120fps preserves PCM across rounded frame boundaries", () =>
    {
        const int sampleRate = 44100;
        const int seconds = 2;
        var source = new AudioBuffer(new AudioFormat { SampleRate = sampleRate, Channels = 2 }, sampleRate * seconds);
        for (int frame = 0; frame < source.Samples; frame++)
        {
            source[0, frame] = (float)Math.Sin(frame * .1);
            source[1, frame] = (float)Math.Cos(frame * .17);
        }
        foreach (double sourceOffsetMs in new[] { 0d, -1000d, -5d, 10d, .5 / sampleRate * 1000 })
        {
            var segments = new List<MusicPlaybackSegment>();
            for (int frame = 0; frame < seconds * 120; frame++)
            {
                double outputStart = frame * 1000d / 120;
                double end = (frame + 1) * 1000d / 120;
                segments.Add(MusicPlaybackSegment.FromClockInterval(outputStart, outputStart + sourceOffsetMs,
                    end - outputStart, 1, 1, sampleRate));
            }
            var output = Render(source, segments, sampleRate * seconds);
            var expected = Render(source, [Segment(0, sampleRate * seconds, sourceOffsetMs / 1000)], sampleRate * seconds);
            double maximumDifference = output.Zip(expected, (actual, wanted) => Math.Abs(actual - wanted)).Max();
            Assert(maximumDifference < 1e-6, $"quantized capture with offset {sourceOffsetMs}ms: max difference {maximumDifference}");
        }
    });
    Check("silence covers negative lead-in, output gaps and EOF", () =>
    {
        var source = Source(100, 1);
        source.Data.AsSpan().Fill(.5f);
        var output = Render(source, [Segment(3, 15, -5d / 48000), Segment(20, 30, 98d / 48000)], 35);
        for (int frame = 0; frame < output.Length; frame++)
            Assert(output[frame] == (frame is >= 8 and < 15 or >= 20 and < 22 ? .5f : 0), $"silence mismatch at {frame}");
    });
    Check("seek changes source position without changing output length", () =>
    {
        var source = Source(1000, 1);
        for (int frame = 0; frame < source.Samples; frame++) source[0, frame] = frame;
        var output = Render(source, [Segment(0, 50, 500d / 48000), Segment(50, 100, 100d / 48000)], 100);
        for (int frame = 0; frame < output.Length; frame++)
            Assert(output[frame] == (frame < 50 ? 500 + frame : 100 + frame - 50), $"seek mismatch at {frame}");
    });
    Check("variable speed changes tone pitch without accumulating timing drift", () =>
    {
        var source = Tone(48000, 1000);
        var output = Render(source,
            [Segment(0, 12000), Segment(12000, 24000, .25, 1.5), Segment(24000, 33000, .625, 2)], 33000);
        Assert(Math.Abs(Frequency(output, 200, 11800) - 1000) < 8, "unity pitch");
        Assert(Math.Abs(Frequency(output, 12200, 23800) - 1500) < 8, "1.5x pitch");
        Assert(Math.Abs(Frequency(output, 24200, 32800) - 2000) < 8, "2x pitch");
    });
    Check("downsampling rejects frequencies above the new Nyquist limit", () =>
    {
        var low = Render(Tone(48000, 3000), [Segment(0, 24000, 0, 2)], 24000);
        var high = Render(Tone(48000, 18000), [Segment(0, 24000, 0, 2)], 24000);
        double passEnergy = Energy(low, 200, 23800);
        double aliasEnergy = Energy(high, 200, 23800);
        Assert(passEnergy > .45, "passband tone attenuated");
        Assert(aliasEnergy < passEnergy * 1e-5, $"alias energy ratio {aliasEnergy / passEnergy}");
    });
    Check("fractional source position is continuous across render blocks", () =>
    {
        var source = Tone(20000, 300);
        var together = Render(source, [Segment(0, 10000, .5 / 48000, .75)], 10000);
        var split = Render(source,
            [Segment(0, 4321, .5 / 48000, .75), Segment(4321, 10000, (.5 + 4321 * .75) / 48000, .75)], 10000);
        Assert(together.AsSpan().SequenceEqual(split), "block/segment phase drift");
    });
    Check("empty timeline produces exact-length silence", () =>
    {
        var output = Render(Source(5, 2), [], 420);
        Assert(output.Length == 840 && output.All(value => value == 0), "empty timeline");
    });
    Check("invalid and overlapping segments are rejected", () =>
    {
        Throws<ArgumentException>(() => Render(Source(100, 1), [Segment(0, 10), Segment(9, 20)], 20));
        Throws<ArgumentException>(() => Render(Source(100, 1), [Segment(0, 21)], 20));
        Throws<ArgumentException>(() => Render(Source(100, 1), [Segment(0, 10, frequency: 0)], 20));
        Throws<ArgumentException>(() => Render(Source(100, 1), [Segment(0, 10, tempo: double.NaN)], 20));
        Throws<ArgumentException>(() => Render(Source(100, 1), [Segment(0, 10, double.PositiveInfinity)], 20));
    });
    Check("tempo commands use source time and invariant numbers", () =>
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            var segments = new[] { Segment(0, 24000, 10, 1, 2), Segment(24000, 48000, 11, .75, 1.5) };
            string commands = DynamicMusicRenderer.BuildCommands(segments, 0, segments.Length, .25, 48000);
            Assert(commands.StartsWith("1.25-", StringComparison.Ordinal), commands);
            Assert(commands.Contains(" rubberband@music tempo 1.125, rubberband@music pitch 0.75;"), commands);
            Assert(!commands.Contains("0.75 rubberband@music tempo"), "commands incorrectly used output time");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    });
    Check("constant tempo run still supplies a valid command file", () =>
    {
        string commands = DynamicMusicRenderer.BuildCommands([Segment(0, 100, tempo: 1.5)], 0, 1, .25, 48000);
        Assert(commands == "0 rubberband@music tempo 1.5;\n", commands);
    });
    if (Environment.GetEnvironmentVariable("ORV_MEASURE_MUSIC") == "1")
        Check("30s stereo dynamic frequency rendering remains finite and reports elapsed time", () =>
        {
            const int sampleRate = 48000;
            const int outputFrames = 30 * sampleRate;
            var source = new AudioBuffer(new AudioFormat { SampleRate = sampleRate, Channels = 2 }, 60 * sampleRate);
            for (int frame = 0; frame < source.Samples; frame++)
            {
                source[0, frame] = (float)Math.Sin(2 * Math.PI * 440 * frame / sampleRate);
                source[1, frame] = (float)Math.Sin(2 * Math.PI * 660 * frame / sampleRate);
            }
            var segments = new List<MusicPlaybackSegment>();
            double sourceTime = 0;
            for (int frame = 0; frame < 30 * 120; frame++)
            {
                double rate = .5 + 1.5 * frame / (30 * 120 - 1d);
                segments.Add(MusicPlaybackSegment.FromClockInterval(frame * 1000d / 120, sourceTime, 1000d / 120, rate, 1, sampleRate));
                sourceTime += 1000d / 120 * rate;
            }
            var timer = Stopwatch.StartNew();
            var output = Render(source, segments, outputFrames);
            timer.Stop();
            Assert(output.All(float.IsFinite), "non-finite music sample");
            Console.WriteLine(FormattableString.Invariant($"MEASURE 30s stereo48k .5→2x: {timer.Elapsed.TotalSeconds:F3}s ({timer.Elapsed.TotalSeconds / 30:F3}x audio duration)"));
        });

    string ffmpeg = Environment.GetEnvironmentVariable("ORV_FFMPEG");
    if (string.IsNullOrWhiteSpace(ffmpeg))
        Console.WriteLine("SKIP native FFmpeg checks (set ORV_FFMPEG)");
    else
    {
        string mode = Environment.GetEnvironmentVariable("ORV_FFMPEG_HAS_RUBBERBAND");
        if (mode != "1")
            Check("missing rubberband fails clearly without a pitch-changing fallback", () =>
            {
                string path = Path.Combine(directory, "missing-filter.pcm");
                try
                {
                    DynamicMusicRenderer.Render(Tone(4800, 440), [Segment(0, 2400, tempo: 2)], 2400, path, ffmpeg);
                    throw new Exception("Missing filter was not rejected");
                }
                catch (InvalidOperationException error)
                {
                    Assert(error.Message.Contains("rubberband", StringComparison.OrdinalIgnoreCase), error.Message);
                    Assert(!File.Exists(path), "failed output was left behind");
                }
            });
        else
        {
            Check("finite command windows preserve every due native setter at 120fps", () =>
            {
                foreach (int sampleRate in new[] { 48000, 44100 })
                {
                    var source = new AudioBuffer(new AudioFormat { SampleRate = sampleRate, Channels = 2 }, sampleRate);
                    for (int frame = sampleRate / 4; frame < source.Samples; frame++)
                    {
                        double time = (frame - sampleRate / 4) / (double)sampleRate;
                        source[0, frame] = (float)(.25 * Math.Sin(2 * Math.PI * 440 * time) + .1 * Math.Sin(2 * Math.PI * 997 * time));
                        source[1, frame] = (float)(.2 * Math.Sin(2 * Math.PI * 660 * time));
                    }
                    var segments = new List<MusicPlaybackSegment>();
                    double sourceTime = 0;
                    for (int frame = 0; frame < 96; frame++)
                    {
                        long start = (long)Math.Round(frame * sampleRate / 120d);
                        long end = (long)Math.Round((frame + 1) * sampleRate / 120d);
                        if (frame == 48)
                        {
                            segments.Add(new(start, start, sourceTime, 1.1, 1.1));
                            segments.Add(new(start, start, sourceTime, .6, .6));
                        }
                        double rate = .5 + .01 * Math.Sin(frame * .23);
                        double frequency = frame < 32 ? 1 : frame < 64 ? 1.4 : .75;
                        segments.Add(new(start, end, sourceTime, frequency, rate / frequency));
                        sourceTime += (end - start) / (double)sampleRate * rate;
                    }
                    string baseline = OpenEndedCommands(segments, .25);
                    string finite = DynamicMusicRenderer.BuildCommands(segments, 0, segments.Count, .25, sampleRate);
                    byte[] expected = RenderCommandScript(source, baseline, segments[0], ffmpeg);
                    byte[] actual = RenderCommandScript(source, finite, segments[0], ffmpeg);
                    Assert(expected.Length > 0 && actual.AsSpan().SequenceEqual(expected),
                        $"finite command intervals changed native PCM at {sampleRate}Hz ({actual.Length} vs {expected.Length} bytes)");
                }
            });
            Check("native tempo changes duration while preserving tone pitch", () =>
            {
                var source = Tone(96000, 1000);
                var output = Render(source, [Segment(0, 48000, 0, 1, 1.5), Segment(48000, 60000, 1.5, 1, 2)], 60000, ffmpeg);
                Assert(output.Length == 60000, "native sample count");
                double before = StableFrequency(output, 3000, 44000);
                double after = StableFrequency(output, 50000, 58000);
                Console.WriteLine(FormattableString.Invariant($"MEASURE native preserved pitch: {before:F3}Hz before, {after:F3}Hz after tempo command"));
                Assert(Math.Abs(before - 1000) < 8, $"native pitch was changed: {before}");
                Assert(Math.Abs(after - 1000) < 8, $"native pitch after tempo command: {after}, energy {Energy(output, 50000, 58000)}");
            });
            Check("native impulse stays within one 60Hz frame of the timeline", () =>
            {
                var source = Source(96000, 1);
                source[0, 24000] = 1;
                var output = Render(source, [Segment(0, 64000, 0, 1, 1.5)], 64000, ffmpeg);
                int peak = Enumerable.Range(0, output.Length).MaxBy(index => Math.Abs(output[index]));
                Console.WriteLine(FormattableString.Invariant($"MEASURE native startup impulse residual: {peak - 16000} samples ({(peak - 16000) / 48d:F3}ms)"));
                Assert(Math.Abs(peak - 16000) <= 800, $"impulse at {peak}, expected 16000");
            });
            Check("native tempo command keeps later transients aligned", () =>
            {
                var source = Source(144000, 1);
                source[0, 24000] = 1;
                source[0, 96000] = 1;
                var output = Render(source,
                    [Segment(0, 48000, 0, 1, 1.5), Segment(48000, 84000, 1.5, 1, 2)], 84000, ffmpeg);
                foreach (int expected in new[] { 16000, 60000 })
                {
                    int peak = Enumerable.Range(expected - 3000, 6000).MaxBy(index => Math.Abs(output[index]));
                    Console.WriteLine(FormattableString.Invariant($"MEASURE native changing-tempo impulse residual at {expected}: {peak - expected} samples ({(peak - expected) / 48d:F3}ms)"));
                    Assert(Math.Abs(peak - expected) <= 800, $"changing-tempo impulse at {peak}, expected {expected}");
                }
            });
            Check("native frequency and tempo remain independent across commands", () =>
            {
                var source = Tone(96000, 1000);
                var output = Render(source,
                    [Segment(0, 48000, 0, 1.25, 1.2), Segment(48000, 60000, 1.5, .75, 2)], 60000, ffmpeg);
                double before = StableFrequency(output, 3000, 44000);
                double after = StableFrequency(output, 51000, 58000);
                Assert(Math.Abs(before - 1250) < 8, $"combined frequency/tempo pitch before command: {before}");
                Assert(Math.Abs(after - 750) < 8, $"combined frequency/tempo pitch after command: {after}");
            });
            Check("native rendering keeps negative source time, gaps and EOF exactly silent", () =>
            {
                var source = Source(48000, 1);
                source.Data.AsSpan().Fill(.5f);
                var output = Render(source,
                    [Segment(0, 24000, -.1, 1, 2), Segment(30000, 42000, .9, 1, 2)], 42000, ffmpeg);
                for (int index = 0; index < output.Length; index++)
                    if (index < 2400 || index is >= 24000 and < 30000 || index >= 32400)
                        Assert(output[index] == 0, $"native silence mismatch at {index}");
                Assert(Energy(output, 4000, 20000) > .01, "native music is missing");
                Assert(Energy(output, 30200, 32000) > .01, "native source after seek is missing");
            });
        }
    }
    Console.WriteLine($"PASS {passed} dynamic music checks");
}
finally
{
    Directory.Delete(directory, true);
}

void Check(string name, Action body)
{
    body();
    passed++;
    Console.WriteLine($"PASS {name}");
}

float[] Render(AudioBuffer source, IReadOnlyList<MusicPlaybackSegment> segments, long count, string ffmpeg = null)
{
    string path = Path.Combine(directory, $"{Guid.NewGuid():N}.pcm");
    DynamicMusicRenderer.Render(source, segments, count, path, ffmpeg);
    byte[] bytes = File.ReadAllBytes(path);
    Assert(bytes.LongLength == count * source.Format.Channels * sizeof(float), "exact PCM length");
    return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
}

byte[] RenderCommandScript(AudioBuffer source, string commands, MusicPlaybackSegment initial, string ffmpeg)
{
    string runDirectory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(runDirectory);
    File.WriteAllBytes(Path.Combine(runDirectory, "source.pcm"), MemoryMarshal.AsBytes(source.Data.AsSpan()).ToArray());
    File.WriteAllText(Path.Combine(runDirectory, "commands.txt"), commands, new UTF8Encoding(false));
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo(ffmpeg)
        {
            WorkingDirectory = runDirectory,
            UseShellExecute = false,
            RedirectStandardError = true
        }
    };
    string filter = FormattableString.Invariant($"asetnsamples=n=256:p=0,asendcmd=f=commands.txt,rubberband@music=tempo={initial.Frequency * initial.Tempo:R}:pitch={initial.Frequency:R}:window=short");
    foreach (string argument in new[]
             {
                 "-hide_banner", "-v", "error", "-nostdin", "-y", "-f", "f32le",
                 "-ar", source.Format.SampleRate.ToString(CultureInfo.InvariantCulture), "-ac", "2", "-i", "source.pcm",
                 "-af", filter, "-f", "f32le", "-c:a", "pcm_f32le", "rendered.pcm"
             })
        process.StartInfo.ArgumentList.Add(argument);
    process.Start();
    var diagnostics = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    Assert(process.ExitCode == 0, $"native command parity render failed: {diagnostics.GetAwaiter().GetResult()}");
    return File.ReadAllBytes(Path.Combine(runDirectory, "rendered.pcm"));
}

static string OpenEndedCommands(IReadOnlyList<MusicPlaybackSegment> segments, double prefixSeconds)
{
    var commands = new StringBuilder();
    var previous = segments[0];
    for (int index = 1; index < segments.Count; index++)
    {
        var segment = segments[index];
        if (segment.Frequency == previous.Frequency && segment.Tempo == previous.Tempo) continue;
        double time = prefixSeconds + segment.SourceStartSeconds - segments[0].SourceStartSeconds;
        commands.Append(FormattableString.Invariant($"{time:R} rubberband@music tempo {segment.Frequency * segment.Tempo:R}, rubberband@music pitch {segment.Frequency:R};\n"));
        previous = segment;
    }
    return commands.ToString();
}

static AudioBuffer Source(int frames, int channels) => new(new AudioFormat { SampleRate = 48000, Channels = channels }, frames);

static AudioBuffer Tone(int frames, double frequency)
{
    var source = Source(frames, 1);
    for (int frame = 0; frame < frames; frame++) source[0, frame] = (float)Math.Sin(2 * Math.PI * frequency * frame / 48000);
    return source;
}

static MusicPlaybackSegment Segment(long start, long end, double source = 0, double frequency = 1, double tempo = 1) =>
    new(start, end, source, frequency, tempo);

static double Frequency(float[] samples, int first, int end)
{
    int crossings = 0;
    for (int index = first + 1; index < end; index++)
        if (samples[index - 1] < 0 && samples[index] >= 0) crossings++;
    return crossings * 48000d / (end - first);
}

static double StableFrequency(float[] samples, int first, int end)
{
    var periods = new List<double>();
    double? previous = null;
    for (int index = first + 1; index < end; index++)
    {
        if (samples[index - 1] >= 0 || samples[index] < 0) continue;
        double crossing = index - samples[index] / (samples[index] - samples[index - 1]);
        if (previous != null) periods.Add(crossing - previous.Value);
        previous = crossing;
    }
    periods.Sort();
    return periods.Count == 0 ? 0 : 48000 / periods[periods.Count / 2];
}

static double Energy(float[] samples, int first, int end)
{
    double energy = 0;
    for (int index = first; index < end; index++) energy += samples[index] * samples[index];
    return energy / (end - first);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
