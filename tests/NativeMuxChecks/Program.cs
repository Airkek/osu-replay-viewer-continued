using System.Buffers.Binary;
using FFmpeg.AutoGen;
using osu_replay_renderer_netcore.CustomHosts.Record;
using osu_replay_renderer_netcore.Audio.Conversion;
using static FFmpeg.AutoGen.ffmpeg;

unsafe class Program
{
    static int Main()
    {
        string libraries = Environment.GetEnvironmentVariable("ORV_FFMPEG_NATIVE_LIBS");
        string executable = Environment.GetEnvironmentVariable("ORV_FFMPEG");
        if (string.IsNullOrWhiteSpace(libraries) || string.IsNullOrWhiteSpace(executable))
        {
            Console.WriteLine("SKIP native mux checks: set ORV_FFMPEG_NATIVE_LIBS and ORV_FFMPEG");
            return 0;
        }
        ffmpeg.RootPath = libraries;
        FFmpegAudioTools.FFmpegExec = executable;
        int passed = 0;
        string directory = Path.Combine(Path.GetTempPath(), "orv-native-mux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (int rate in new[] { 22050, 44100, 48000, 96000 })
                foreach (int channels in new[] { 1, 2 })
                {
                    RoundTrip(Path.Combine(directory, $"audio {rate}-{channels}.mp4"), rate, channels);
                    Console.WriteLine($"PASS native AAC mux {rate} Hz / {channels} channels");
                    passed++;
                }
            NativeVideoAudioRoundTrip(Path.Combine(directory, "native video and audio.mp4"), 30, 30);
            NativeVideoAudioRoundTrip(Path.Combine(directory, "native 60fps video and audio.mp4"), 60, 121);
            Console.WriteLine("PASS native video and audio preserve every frame and CFR sample duration at 30/60 FPS");
            passed++;
            RejectIncompletePcm(Path.Combine(directory, "invalid.mp4"));
            Console.WriteLine("PASS native mux rejects incomplete PCM frames");
            passed++;
            RejectUnsupportedRate(Path.Combine(directory, "rate.mp4"));
            Console.WriteLine("PASS native AAC unsupported rate cleans up");
            passed++;
            Console.WriteLine($"{passed} native mux checks passed");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    static void RoundTrip(string path, int rate, int channels)
    {
        AVFormatContext* format = Open(path);
        NativeAudioMuxer audio = null;
        int samples = rate + 137;
        try
        {
            audio = new NativeAudioMuxer(format, rate, channels);
            Check(avformat_write_header(format, null));
            byte[] pcm = new byte[samples * channels * sizeof(short)];
            for (int i = 0; i < samples; i++)
                for (int ch = 0; ch < channels; ch++)
                {
                    short sample = (short)(Math.Sin(i * 2 * Math.PI * 440 / rate) * 16000 * (ch == 0 ? 1 : .5));
                    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((i * channels + ch) * 2), sample);
                }
            int offset = 0;
            int[] chunks = { 1, 17, 1024, 735, 3, 2048 };
            int chunk = 0;
            while (offset < samples)
            {
                int count = Math.Min(chunks[chunk++ % chunks.Length], samples - offset);
                audio.Write(pcm.AsSpan(offset * channels * 2, count * channels * 2));
                offset += count;
            }
            audio.Finish();
            audio.Finish();
            try
            {
                audio.Write(pcm.AsSpan(0, channels * 2));
                throw new Exception("A finished stream accepted PCM");
            }
            catch (InvalidOperationException) { }
            Check(av_write_trailer(format));
        }
        finally
        {
            audio?.Dispose();
            audio?.Dispose();
            Close(format);
        }
        var decoded = FFmpegAudioTools.Decode(path, outChannels: channels);
        if (decoded.Format.SampleRate != rate || decoded.Format.Channels != channels)
            throw new Exception("AAC output changed the song rate or channel count");
        if (decoded.Samples < samples - 1 || decoded.Samples > samples + 1024)
            throw new Exception($"Unexpected AAC duration: {decoded.Samples}, wanted {samples}");
        double energy = 0;
        for (int i = 0; i < samples; i++) energy += decoded[0, i] * decoded[0, i];
        if (energy / samples < .04) throw new Exception("AAC output is silent or corrupted");
    }

    static void NativeVideoAudioRoundTrip(string path, int fps, int frames)
    {
        var config = new EncoderConfig
        {
            FPS = fps,
            Resolution = new System.Drawing.Size(16, 16),
            OutputPath = path,
            Encoder = "mpeg4",
            Preset = "slow",
            Bitrate = "1M",
            FFmpegPath = ffmpeg.RootPath,
            PixelFormat = PixelFormatMode.YUV420,
            ColorSpace = ColorSpaceMode.BT709
        };
        var encoder = new FFmpegAutoGenEncoder(config);
        encoder.ConfigureAudio(48000, 2);
        try
        {
            encoder.Start();
            byte[] video = new byte[16 * 16 * 3 / 2];
            video.AsSpan(16 * 16).Fill(128);
            int samplesPerFrame = 48000 / fps;
            byte[] audio = new byte[samplesPerFrame * 2 * 2];
            for (int frame = 0; frame < frames; frame++)
            {
                video.AsSpan(0, 16 * 16).Fill((byte)(20 + frame * 180 / frames));
                encoder.WriteFrame(video);
                for (int sample = 0; sample < samplesPerFrame; sample++)
                {
                    short value = (short)(Math.Sin((frame * samplesPerFrame + sample) * Math.PI * 2 * 440 / 48000) * 16000);
                    BinaryPrimitives.WriteInt16LittleEndian(audio.AsSpan(sample * 4), value);
                    BinaryPrimitives.WriteInt16LittleEndian(audio.AsSpan(sample * 4 + 2), value);
                }
                encoder.WriteAudio(audio);
            }
        }
        finally { encoder.Finish(); }
        encoder.Finish();
        AssertVideoSampleTiming(path, fps, frames);
        var decoded = FFmpegAudioTools.Decode(path);
        int expectedSamples = 48000 / fps * frames;
        if (decoded.Format.SampleRate != 48000 || decoded.Samples < expectedSamples || decoded.Samples > expectedSamples + 1024)
            throw new Exception("Native A/V mux audio duration is incorrect");
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo(FFmpegAudioTools.FFmpegExec)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (string value in new[] { "-v", "error", "-i", path, "-vsync", "0", "-map", "0:v:0", "-pix_fmt", "gray", "-f", "rawvideo", "-" })
            process.StartInfo.ArgumentList.Add(value);
        process.Start();
        var errors = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception(errors.GetAwaiter().GetResult());
        if (output.Length != frames * 16 * 16) throw new Exception("Native A/V mux lost video frames");
        var bytes = output.ToArray();
        for (int frame = 1; frame < frames; frame++)
            if (bytes[frame * 256] < bytes[(frame - 1) * 256]) throw new Exception("Native A/V mux reordered video frames");
    }

    static void AssertVideoSampleTiming(string path, int fps, int frames)
    {
        ReadOnlyMemory<byte> movie = FindBox(File.ReadAllBytes(path), "moov");
        ReadOnlyMemory<byte> media = FindBox(FindBox(movie, "trak"), "mdia");
        ReadOnlySpan<byte> header = FindBox(media, "mdhd").Span;
        bool versionOne = header[0] == 1;
        uint timescale = BinaryPrimitives.ReadUInt32BigEndian(header[(versionOne ? 20 : 12)..]);
        ulong declaredDuration = versionOne
            ? BinaryPrimitives.ReadUInt64BigEndian(header[24..])
            : BinaryPrimitives.ReadUInt32BigEndian(header[16..]);
        ReadOnlySpan<byte> timing = FindBox(FindBox(FindBox(media, "minf"), "stbl"), "stts").Span;
        uint entries = BinaryPrimitives.ReadUInt32BigEndian(timing[4..]);
        ulong count = 0;
        ulong duration = 0;
        for (int entry = 0; entry < entries; entry++)
        {
            int offset = 8 + entry * 8;
            uint samples = BinaryPrimitives.ReadUInt32BigEndian(timing[offset..]);
            uint ticks = BinaryPrimitives.ReadUInt32BigEndian(timing[(offset + 4)..]);
            if (ticks == 0) throw new Exception("The MP4 contains a zero-duration video sample");
            count += samples;
            duration += (ulong)samples * ticks;
        }
        if (count != (ulong)frames || duration * (ulong)fps != (ulong)frames * timescale || declaredDuration != duration)
            throw new Exception("The MP4 video sample timing does not preserve the complete CFR clip");
    }

    static ReadOnlyMemory<byte> FindBox(ReadOnlyMemory<byte> data, string name)
    {
        for (int offset = 0; offset + 8 <= data.Length;)
        {
            ReadOnlySpan<byte> header = data.Span[offset..];
            int size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header));
            if (size < 8 || size > data.Length - offset) throw new Exception("Invalid MP4 box size");
            if (System.Text.Encoding.ASCII.GetString(header.Slice(4, 4)) == name)
                return data.Slice(offset + 8, size - 8);
            offset += size;
        }
        throw new Exception($"MP4 box {name} is missing");
    }

    static void RejectIncompletePcm(string path)
    {
        AVFormatContext* format = Open(path);
        try
        {
            using var audio = new NativeAudioMuxer(format, 48000, 2);
            Check(avformat_write_header(format, null));
            try { audio.Write(new byte[3]); }
            catch (ArgumentException) { return; }
            throw new Exception("An incomplete PCM frame was accepted");
        }
        finally { Close(format); }
    }

    static void RejectUnsupportedRate(string path)
    {
        AVFormatContext* format = Open(path);
        try
        {
            try { using var audio = new NativeAudioMuxer(format, 12345, 2); }
            catch (IOException) { return; }
            throw new Exception("An unsupported AAC rate was accepted");
        }
        finally { Close(format); }
    }

    static AVFormatContext* Open(string path)
    {
        AVFormatContext* format = null;
        Check(avformat_alloc_output_context2(&format, null, null, path));
        Check(avio_open(&format->pb, path, AVIO_FLAG_WRITE));
        return format;
    }

    static void Close(AVFormatContext* format)
    {
        if (format == null) return;
        if (format->pb != null) avio_closep(&format->pb);
        avformat_free_context(format);
    }

    static void Check(int status)
    {
        if (status < 0) throw new IOException($"FFmpeg operation failed: {status}");
    }
}
