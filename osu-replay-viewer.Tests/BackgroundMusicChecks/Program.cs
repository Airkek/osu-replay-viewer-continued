using System.Diagnostics;
using System.Runtime.InteropServices;
using osu_replay_renderer_netcore.Audio;
using osu_replay_renderer_netcore.Audio.Conversion;

string ffmpeg = Environment.GetEnvironmentVariable("ORV_FFMPEG") ?? "ffmpeg";
string directory = Path.Combine(Path.GetTempPath(), $"orv-background-checks-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
int passed = 0;
try
{
    Check("only finite non-unity-tempo runs start speculation", () =>
    {
        Assert(!BackgroundMusicRenderer.CanStart(new(0, 100, 0, 1, 1)), "unity tempo should keep the foreground path");
        Assert(!BackgroundMusicRenderer.CanStart(new(0, 100, 0, 1, double.NaN)), "NaN tempo accepted");
        Assert(!BackgroundMusicRenderer.CanStart(new(0, 0, 0, 1, 1.5)), "empty run accepted");
        Assert(BackgroundMusicRenderer.CanStart(new(0, 100, 0, 1, 1.5)), "constant tempo is ineligible");
    });
    foreach (int sampleRate in new[] { 44100, 48000 })
    {
        foreach (var rates in new[] { (1d, 1.5), (1.25, .8), (.75, 1.25) })
        {
            foreach (double sourceStart in new[] { -.1, .5 / sampleRate, .123456789 })
            {
                Check($"{sampleRate}Hz constant {rates} source={sourceStart:R} matches exact foreground PCM", () =>
                {
                    var source = Tone(sampleRate, sampleRate * 2);
                    var segments = Segments(sampleRate, sourceStart, rates.Item1, rates.Item2, 120);
                    Compare(source, segments, waitForWorker: sourceStart < 0);
                });
            }
        }
    }
    Check("a coalesced 2000-frame timeline keeps one pending horizon and matches PCM", () =>
    {
        var source = Tone(48000, 48000);
        Compare(source, Segments(48000, 0, 1, 1.5, 2000), waitForWorker: false);
    });
    Check("tempo, pitch, seek and output-gap changes reject the speculative PCM", () =>
    {
        var source = Tone(48000, 48000 * 2);
        var original = Segments(48000, 0, 1, 1.5, 12);
        foreach (string change in new[] { "tempo", "pitch", "seek", "gap" })
        {
            var segments = original.ToArray();
            segments[3] = change switch
            {
                "tempo" => segments[3] with { Tempo = 1.6 },
                "pitch" => segments[3] with { Frequency = .9 },
                "seek" => segments[3] with { SourceStartSeconds = 1.5 },
                _ => segments[3] with { OutputStartSample = segments[3].OutputStartSample + 1 }
            };
            using var background = new BackgroundMusicRenderer(source.Format, _ => source.CreateCopy(), segments[0], ffmpeg);
            for (int index = 1; index < segments.Length; index++) background.Observe(segments[index]);
            string path = Path.Combine(directory, "rejected.f32");
            Assert(!background.TryComplete(segments, segments[^1].OutputEndSample, path, out string reason), change + " was accepted");
            Assert(!string.IsNullOrWhiteSpace(reason), "missing fallback reason");
            string actual = Path.Combine(directory, "fallback.f32");
            string expected = Path.Combine(directory, "dynamic-reference.f32");
            DynamicMusicRenderer.Render(source, segments, segments[^1].OutputEndSample, actual, ffmpeg);
            DynamicMusicRenderer.Render(source, segments, segments[^1].OutputEndSample, expected, ffmpeg);
            Assert(File.ReadAllBytes(actual).AsSpan().SequenceEqual(File.ReadAllBytes(expected)), "dynamic fallback PCM changed");
        }
    });
    Check("final timeline validation rejects a changed or missing observation", () =>
    {
        var source = Tone(48000, 48000);
        var segments = Segments(48000, 0, 1, 1.5, 8);
        using var background = new BackgroundMusicRenderer(source.Format, _ => source.CreateCopy(), segments[0], ffmpeg);
        Assert(!background.TryComplete(segments, segments[^1].OutputEndSample, Path.Combine(directory, "unobserved.f32"), out _),
            "an unobserved final timeline was accepted");
    });
    Check("a failed worker falls back without consuming or disposing the foreground source", () =>
    {
        var source = Tone(48000, 48000);
        var segments = Segments(48000, 0, 1, 1.5, 8);
        using var background = new BackgroundMusicRenderer(source.Format, _ => throw new IOException("synthetic worker failure"),
            segments[0], ffmpeg);
        for (int index = 1; index < segments.Length; index++) background.Observe(segments[index]);
        Assert(!background.TryComplete(segments, segments[^1].OutputEndSample, Path.Combine(directory, "failed.f32"), out string reason),
            "failed worker was accepted");
        Assert(reason.Contains("synthetic worker failure"), "worker failure was hidden");
        DynamicMusicRenderer.Render(source, segments, segments[^1].OutputEndSample, Path.Combine(directory, "after-failure.f32"), ffmpeg);
    });
    Check("a failed PCM consumer stops the native producer and preserves the failure reason", () =>
    {
        var source = Tone(48000, 48000 * 10);
        var segment = new MusicPlaybackSegment(0, 48000 * 10, 0, 1, 1.5);
        TrackingSource tracked = null;
        using var background = new BackgroundMusicRenderer(source.Format, _ => tracked = new TrackingSource(source.CreateCopy()),
            segment, ffmpeg, path => new FailedConsumer(File.Create(path)));
        string reason = null;
        bool accepted = true;
        var completion = Task.Run(() => accepted = background.TryComplete(new[] { segment }, segment.OutputEndSample,
            Path.Combine(directory, "failed-consumer.f32"), out reason));
        Assert(completion.Wait(TimeSpan.FromSeconds(15)), "failed PCM consumer left the worker blocked");
        Assert(!accepted && reason.Contains("Injected PCM consumer failure"), "consumer failure was masked: " + reason);
        Assert(tracked.Disposed && !background.IsRunning, "consumer failure retained its source or worker");
        Assert(background.NativeProcessId > 0, "consumer check did not start native FFmpeg");
        try
        {
            using var child = Process.GetProcessById(background.NativeProcessId);
            Assert(child.HasExited, "consumer failure retained its native FFmpeg process");
        }
        catch (ArgumentException) { }
    });
    Check("cancellation releases an idle native worker and preserves its independent foreground decoder", () =>
    {
        string wave = Path.Combine(directory, "cancel-source.wav");
        WriteWave(wave, Tone(48000, 48000 * 3));
        using var original = new FFmpegMusicSource(wave, ffmpeg);
        var segments = Segments(48000, 0, 1, 1.5, 8);
        TrackingSource tracked = null;
        var background = new BackgroundMusicRenderer(original.Format, token =>
        {
            var value = new TrackingSource(original.CreateIndependent(token));
            Volatile.Write(ref tracked, value);
            return value;
        }, segments[0], ffmpeg);
        for (int index = 1; index < segments.Length; index++) background.Observe(segments[index]);
        Assert(SpinWait.SpinUntil(() => Volatile.Read(ref tracked)?.ReadCalls > 0, TimeSpan.FromSeconds(15)), "worker did not read before completion");
        var timer = Stopwatch.StartNew();
        background.Dispose();
        Assert(timer.Elapsed < TimeSpan.FromSeconds(5), "cancellation did not promptly release native processes");
        Assert(tracked.Disposed && !background.IsRunning, "worker decoder or task survived disposal");
        var actual = new float[512 * 2];
        ((IPcmSource)original).ReadFrames(0, actual);
        Assert(actual.AsSpan().SequenceEqual(Tone(48000, 48000 * 3).Data.AsSpan(0, actual.Length)), "foreground decoder was changed by cancellation");
    });
    Console.WriteLine($"PASS {passed} background music checks");
}
finally
{
    Directory.Delete(directory, true);
}

void Compare(AudioBuffer source, MusicPlaybackSegment[] segments, bool waitForWorker)
{
    string actual = Path.Combine(directory, "background.f32");
    string expected = Path.Combine(directory, "foreground.f32");
    long total = segments[^1].OutputEndSample + 173;
    DynamicMusicRenderer.Render(source, segments, total, expected, ffmpeg);
    TrackingSource tracked = null;
    using var background = new BackgroundMusicRenderer(source.Format, _ =>
    {
        var value = new TrackingSource(source.CreateCopy());
        Volatile.Write(ref tracked, value);
        return value;
    }, segments[0], ffmpeg);
    for (int index = 1; index < segments.Length; index++)
    {
        background.Observe(segments[index]);
        if (waitForWorker && index == Math.Min(20, segments.Length - 1))
        {
            bool started = SpinWait.SpinUntil(() => Volatile.Read(ref tracked)?.ReadCalls > 0 || !background.IsRunning,
                TimeSpan.FromSeconds(15));
            if (!started || Volatile.Read(ref tracked)?.ReadCalls is not > 0)
            {
                for (int remaining = index + 1; remaining < segments.Length; remaining++) background.Observe(segments[remaining]);
                bool accepted = background.TryComplete(segments, total, actual, out string failure);
                throw new Exception($"Worker did not read after positive horizons; completed={accepted}, reason={failure ?? "none"}");
            }
        }
        if (waitForWorker && index % 20 == 0) Thread.Sleep(1);
    }
    Assert(background.TryComplete(segments, total, actual, out string reason), "constant timeline fell back: " + reason);
    byte[] wanted = File.ReadAllBytes(expected);
    byte[] rendered = File.ReadAllBytes(actual);
    if (!rendered.AsSpan().SequenceEqual(wanted))
    {
        int first = Enumerable.Range(0, Math.Min(rendered.Length, wanted.Length)).FirstOrDefault(index => rendered[index] != wanted[index], -1);
        throw new Exception($"PCM differs at byte {first}; actual {rendered.Length}, expected {wanted.Length}");
    }
    Assert(background.MaximumPendingHorizons == 1 && tracked.MaximumReadFrames <= 16384, "worker memory/read window is unbounded");
    Assert(tracked.Disposed, "completed worker retained its source");
}

MusicPlaybackSegment[] Segments(int sampleRate, double start, double frequency, double tempo, int count)
{
    var segments = new MusicPlaybackSegment[count];
    for (int frame = 0; frame < count; frame++)
    {
        long first = 173 + (long)Math.Round(frame * sampleRate / 60d);
        long end = 173 + (long)Math.Round((frame + 1) * sampleRate / 60d);
        segments[frame] = new(first, end, start + (first - 173) / (double)sampleRate * frequency * tempo, frequency, tempo);
    }
    return segments;
}

AudioBuffer Tone(int sampleRate, int frames)
{
    var source = new AudioBuffer(new AudioFormat { SampleRate = sampleRate, Channels = 2, PCMSize = 2 }, frames);
    for (int frame = 0; frame < frames; frame++)
    {
        source[0, frame] = (float)(.25 * Math.Sin(frame * .14) + .1 * Math.Sin(frame * .31));
        source[1, frame] = (float)(.2 * Math.Cos(frame * .17));
    }
    return source;
}

void WriteWave(string path, AudioBuffer source)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    int bytes = checked(source.Data.Length * sizeof(float));
    writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVEfmt "u8); writer.Write(16);
    writer.Write((short)3); writer.Write((short)source.Format.Channels); writer.Write(source.Format.SampleRate);
    writer.Write(source.Format.SampleRate * source.Format.Channels * sizeof(float));
    writer.Write((short)(source.Format.Channels * sizeof(float))); writer.Write((short)32);
    writer.Write("data"u8); writer.Write(bytes); stream.Write(MemoryMarshal.AsBytes(source.Data.AsSpan()));
}

void Check(string name, Action test)
{
    test();
    passed++;
    Console.WriteLine("PASS " + name);
}

void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class TrackingSource(IPcmSource inner) : IPcmSource
{
    private int readCalls;
    public AudioFormat Format => inner.Format;
    public long Length => inner.Length;
    internal int ReadCalls => Volatile.Read(ref readCalls);
    internal int MaximumReadFrames { get; private set; }
    internal bool Disposed { get; private set; }
    public void ReadFrames(long firstFrame, Span<float> output)
    {
        MaximumReadFrames = Math.Max(MaximumReadFrames, output.Length / Format.Channels);
        inner.ReadFrames(firstFrame, output);
        Interlocked.Increment(ref readCalls);
    }
    public void Dispose()
    {
        inner.Dispose();
        Disposed = true;
    }
}

sealed class FailedConsumer(Stream inner) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Injected PCM consumer failure");
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new IOException("Injected PCM consumer failure"));
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
