using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using osu_replay_renderer_netcore.Audio;
using osu_replay_renderer_netcore.Audio.Conversion;
using osu_replay_renderer_netcore.CustomHosts.Record;

int passed = 0;
Check("native rates and optional padded chunks", () =>
{
    foreach (int rate in new[] { 22050, 44100, 48000, 96000 })
    {
        var decoded = Read(Wave(new[] { .25f, -.25f, .5f, -.5f }, rate, junk: true));
        Assert(decoded.Format.SampleRate == rate && decoded.Samples == 2, "rate/frame count");
        Assert(decoded[1, 1] == -.5f, "float PCM");
    }
});
Check("extensible unknown-size PCM and short reads across blocks", () =>
{
    var values = Signal(AudioBuffer.BlockLength + 14);
    var decoded = Read(Wave(values, 48000, extensible: true, unknown: true), 3);
    Assert(decoded.Samples == values.Length / 2, "unknown length");
    for (int i = 0; i < values.Length; i++)
        Assert(decoded[i % 2, i / 2] == values[i], $"value {i}");
    Assert(decoded.GetReadOnlySpan(AudioBuffer.BlockLength - 2, 8).Length == 2, "bounded segment");
});
Check("known-size truncated PCM is rejected", () =>
{
    var wave = Wave(new[] { .25f, -.25f });
    Throws<EndOfStreamException>(() => Read(wave[..^1]));
});
Check("unknown-size partial float is rejected", () =>
{
    var wave = Wave(new[] { .25f, -.25f }, unknown: true);
    Throws<InvalidDataException>(() => Read(wave[..^1]));
});
Check("incomplete channel frame is rejected", () =>
    Throws<InvalidDataException>(() => Read(Wave(new[] { .25f }, unknown: true))));
Check("invalid sample rates are rejected", () =>
{
    Throws<InvalidDataException>(() => Read(Wave(new[] { .25f, .5f }, 0)));
    var wave = Wave(new[] { .25f, .5f });
    BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24), uint.MaxValue);
    Throws<InvalidDataException>(() => Read(wave));
});
Check("non-float format is rejected", () =>
{
    var wave = Wave(new[] { .25f, .5f });
    BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20), 1);
    Throws<InvalidDataException>(() => Read(wave));
});
Check("segmented music preserves PCM through arbitrary mix chunks", () =>
{
    var values = Signal(AudioBuffer.BlockLength * 2 + 30);
    var segmented = Read(Wave(values));
    var continuous = new AudioBuffer(new AudioFormat(), values.Length / 2);
    values.CopyTo(continuous.Data, 0);
    var expected = Mix(continuous, new[] { continuous.Samples + 4 }, gain: .375);
    var actual = Mix(segmented, new[] { 1, 8190, 2, 7, segmented.Samples - 8196 }, gain: .375);
    Assert(actual.SequenceEqual(expected), "chunk/block boundary PCM mismatch");
});
Check("gain is per voice and shared PCM stays unchanged", () =>
{
    var source = new AudioBuffer(new AudioFormat(), 2);
    source.Data.AsSpan().Fill(.5f);
    var mixer = new StreamingAudioMixer(new AudioFormat());
    mixer.AddVoice(source, .25);
    mixer.AddVoice(source, .5);
    var output = mixer.MixChunk(2);
    short expected = Pcm(.5f * .75f);
    for (int i = 0; i < output.Length; i += 2)
        Assert(BinaryPrimitives.ReadInt16LittleEndian(output[i..]) == expected, "voice gain");
    Assert(source.Data.All(value => value == .5f), "source mutation");
});
Check("mono resampling applies gain and duplicates channels", () =>
{
    var source = new AudioBuffer(new AudioFormat { Channels = 1, SampleRate = 24000 }, 3);
    new[] { 0f, .5f, 1f }.CopyTo(source.Data, 0);
    var mixer = new StreamingAudioMixer(new AudioFormat { SampleRate = 48000 });
    mixer.AddVoice(source, .5);
    var output = mixer.MixChunk(6).ToArray();
    for (int frame = 0; frame < 6; frame++)
    {
        short expected = Pcm(new[] { 0f, .125f, .25f, .375f, .5f, .5f }[frame]);
        Assert(BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(frame * 4)) == expected, "resampling");
        Assert(BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(frame * 4 + 2)) == expected, "mono duplication");
    }
});
Check("segmented source can seek forward and backward", () =>
{
    var source = Read(Wave(Signal(AudioBuffer.BlockLength + 20)));
    var mixer = new StreamingAudioMixer(new AudioFormat());
    var voice = mixer.AddVoice(source);
    foreach (int position in new[] { 8190, 0, 8195, 17 })
    {
        voice.Position = position;
        var output = mixer.MixChunk(2);
        Assert(BinaryPrimitives.ReadInt16LittleEndian(output) == Pcm(source[0, position]), "seek position");
    }
});
Check("stopped and exhausted voices leave silence", () =>
{
    var source = new AudioBuffer(new AudioFormat(), 1);
    source.Data.AsSpan().Fill(.5f);
    var mixer = new StreamingAudioMixer(new AudioFormat());
    mixer.AddVoice(source).Stopped = true;
    Assert(mixer.MixChunk(3).ToArray().All(value => value == 0), "stopped voice");
    mixer.AddVoice(source);
    mixer.MixChunk(1);
    Assert(mixer.MixChunk(3).ToArray().All(value => value == 0), "exhausted voice");
});
Check("steady-state mixing reuses working buffers", () =>
{
    var source = new AudioBuffer(new AudioFormat(), 100000);
    var mixer = new StreamingAudioMixer(new AudioFormat());
    mixer.AddVoice(source);
    for (int i = 0; i < 10; i++) mixer.MixChunk(32);
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 1000; i++) mixer.MixChunk(32);
    Assert(GC.GetAllocatedBytesForCurrentThread() == before, "per-chunk allocation");
});
Check("PCM cache identity and handle generation", () =>
{
    var cache = new SamplePcmCache();
    var owner = new object();
    int decoded = 0;
    AudioBuffer Decode() { decoded++; return Tone(); }
    var first = cache.Get(owner, 1, 1, Decode);
    Assert(ReferenceEquals(first, cache.Get(owner, 1, 1, Decode)) && decoded == 1, "cache miss");
    Assert(!ReferenceEquals(first, cache.Get(owner, 2, 1, Decode)) && decoded == 2, "stale handle");
    Assert(!ReferenceEquals(first, cache.Get(new object(), 1, 1, Decode)), "different factory collision");
});
Check("pitch variants are reused, immutable and bounded", () =>
{
    var cache = new SamplePcmCache();
    var owner = new object();
    var original = cache.Get(owner, 1, 1, Tone);
    var snapshot = original.Data.ToArray();
    var pitched = cache.Get(owner, 1, 1.1, Tone);
    Assert(ReferenceEquals(pitched, cache.Get(owner, 1, 1.1, Tone)), "pitch cache miss");
    for (int i = 0; i < 8; i++) cache.Get(owner, 1, 1.2 + i * .1, Tone);
    Assert(!ReferenceEquals(pitched, cache.Get(owner, 1, 1.1, Tone)), "unbounded pitch cache");
    Assert(original.Data.SequenceEqual(snapshot), "cached original mutated");
});
Check("failed reload cannot reuse an old handle PCM", () =>
{
    var cache = new SamplePcmCache();
    var owner = new object();
    var previous = cache.Get(owner, 1, 1, Tone);
    Throws<InvalidDataException>(() => cache.Get(owner, 2, 1, () => throw new InvalidDataException()));
    Assert(!ReferenceEquals(previous, cache.Get(owner, 2, 1, Tone)), "old PCM after failed reload");
});
Check("pitch byte budget evicts variants before the count limit", () =>
{
    var cache = new SamplePcmCache();
    var owner = new object();
    AudioBuffer LargeTone() => new AudioBuffer(new AudioFormat(), 400000);
    var first = cache.Get(owner, 1, 1.1, LargeTone);
    cache.Get(owner, 1, 1.2, LargeTone);
    cache.Get(owner, 1, 1.3, LargeTone);
    Assert(!ReferenceEquals(first, cache.Get(owner, 1, 1.1, LargeTone)), "pitch bytes exceed budget");
});
Check("decoded storage has no full PCM staging allocation", () =>
{
    var wave = Wave(Signal(AudioBuffer.BlockLength * 20), unknown: true);
    long before = GC.GetAllocatedBytesForCurrentThread();
    var decoded = Read(wave);
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Assert(allocated < wave.Length + AudioBuffer.BlockLength * sizeof(float) * 3, "extra full PCM copy");
    Assert(decoded.Samples == AudioBuffer.BlockLength * 10, "decoded frame count");
});
Check("cache does not retain sample owners", () =>
{
    var cache = new SamplePcmCache();
    var owner = CacheOwner(cache);
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    Assert(!owner.IsAlive, "factory retained by PCM cache");
    GC.KeepAlive(cache);
});
string ffmpegPath = Environment.GetEnvironmentVariable("ORV_FFMPEG");
if (string.IsNullOrWhiteSpace(ffmpegPath))
{
    Console.WriteLine("SKIP FFmpeg integration checks (set ORV_FFMPEG)");
}
else
{
    string tempDirectory = Path.Combine(Path.GetTempPath(), $"orv-audio-checks-{Guid.NewGuid():N}");
    string previousFFmpeg = FFmpegAudioTools.FFmpegExec;
    Directory.CreateDirectory(tempDirectory);
    FFmpegAudioTools.FFmpegExec = ffmpegPath;
    try
    {
        foreach (int rate in new[] { 22050, 44100, 48000, 96000 })
        {
            int frames = rate / 3 + 11;
            var values = Signal(frames * 2);
            string inputPath = Path.Combine(tempDirectory, $"source {rate}.wav");
            File.WriteAllBytes(inputPath, Wave(values, rate));
            AudioBuffer decoded = null;
            Check($"FFmpeg native {rate} Hz decode", () =>
            {
                decoded = FFmpegAudioTools.Decode(inputPath);
                Assert(decoded.Format.SampleRate == rate && decoded.Samples == frames, "native rate/length changed");
                for (int i = 0; i < values.Length; i++)
                    Assert(decoded[i % 2, i / 2] == values[i], $"float decode {i}");
            });
            Check($"FFmpeg {rate} Hz variable chunks to AAC roundtrip", () =>
            {
                var mixer = new StreamingAudioMixer(new AudioFormat { SampleRate = decoded.Format.SampleRate });
                mixer.AddVoice(decoded);
                string aacPath = Path.Combine(tempDirectory, $"output {rate}.aac");
                var encoder = new ExternalAudioEncoder(aacPath, mixer.Format.SampleRate, mixer.Format.Channels, ffmpegPath);
                encoder.Start();
                try
                {
                    int written = 0;
                    int chunkIndex = 0;
                    int[] chunks = { 1, Math.Max(1, rate / 60), Math.Max(1, rate / 59), 17, 1023 };
                    while (written < frames)
                    {
                        int count = Math.Min(chunks[chunkIndex++ % chunks.Length], frames - written);
                        encoder.Write(mixer.MixChunk(count));
                        written += count;
                    }
                }
                finally
                {
                    encoder.Finish();
                }
                Assert(File.Exists(aacPath) && new FileInfo(aacPath).Length > 0, "AAC output missing");
                var roundtrip = FFmpegAudioTools.Decode(aacPath);
                Assert(roundtrip.Format.SampleRate == rate, "AAC sample rate changed");
                Assert(roundtrip.Samples >= frames && roundtrip.Samples <= frames + 3072, "AAC frame padding");
                double energy = 0;
                for (int i = 0; i < roundtrip.Samples; i++)
                {
                    float sample = roundtrip[0, i];
                    Assert(float.IsFinite(sample), "invalid AAC samples");
                    energy += sample * sample;
                }
                Assert(energy / roundtrip.Samples > .01, "silent AAC output");
            });
        }
        Check("FFmpeg missing input fails", () =>
            Throws<IOException>(() => FFmpegAudioTools.Decode(Path.Combine(tempDirectory, "missing.wav"))));
    }
    finally
    {
        FFmpegAudioTools.FFmpegExec = previousFFmpeg;
        Directory.Delete(tempDirectory, true);
    }
}
Console.WriteLine($"Passed {passed} audio pipeline checks");

void Check(string name, Action action)
{
    action();
    passed++;
    Console.WriteLine($"PASS {name}");
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
static short Pcm(float value) => (short)((float)Math.Tanh(value) * 32767f);
static float[] Signal(int count) => Enumerable.Range(0, count).Select(i => (float)Math.Sin(i * .13) * .5f).ToArray();
static AudioBuffer Tone()
{
    var buffer = new AudioBuffer(new AudioFormat(), 4096);
    Signal(buffer.Data.Length).CopyTo(buffer.Data, 0);
    return buffer;
}
[MethodImpl(MethodImplOptions.NoInlining)]
static WeakReference CacheOwner(SamplePcmCache cache)
{
    var owner = new object();
    cache.Get(owner, 1, 1, Tone);
    return new WeakReference(owner);
}
static byte[] Mix(AudioBuffer buffer, int[] chunks, double gain)
{
    var mixer = new StreamingAudioMixer(new AudioFormat { SampleRate = buffer.Format.SampleRate });
    mixer.AddVoice(buffer, gain);
    using var result = new MemoryStream();
    foreach (int chunk in chunks) result.Write(mixer.MixChunk(chunk));
    return result.ToArray();
}
static AudioBuffer Read(byte[] data, int maxRead = int.MaxValue) => FloatWaveReader.Read(new ShortReadStream(data, maxRead));
static byte[] Wave(float[] values, int rate = 48000, bool extensible = false, bool unknown = false, bool junk = false)
{
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write("RIFF"u8);
    writer.Write(uint.MaxValue);
    writer.Write("WAVE"u8);
    if (junk)
    {
        writer.Write("JUNK"u8);
        writer.Write(3);
        writer.Write(new byte[] { 1, 2, 3, 0 });
    }
    writer.Write("fmt "u8);
    writer.Write(extensible ? 40 : 16);
    writer.Write((ushort)(extensible ? 0xfffe : 3));
    writer.Write((ushort)2);
    writer.Write(rate);
    writer.Write(rate * 8);
    writer.Write((ushort)8);
    writer.Write((ushort)32);
    if (extensible)
    {
        writer.Write((ushort)22);
        writer.Write((ushort)32);
        writer.Write(3);
        writer.Write(new Guid("00000003-0000-0010-8000-00aa00389b71").ToByteArray());
    }
    writer.Write("data"u8);
    writer.Write(unknown ? uint.MaxValue : (uint)values.Length * 4);
    foreach (float value in values) writer.Write(value);
    return stream.ToArray();
}
sealed class ShortReadStream(byte[] bytes, int maxRead) : MemoryStream(bytes)
{
    public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, maxRead)]);
}
