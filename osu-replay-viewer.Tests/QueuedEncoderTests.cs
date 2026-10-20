using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using osu_replay_renderer_netcore.CustomHosts.Record;

internal static class QueuedEncoderTests
{
    private static int Main()
    {
        Action[] tests =
        [
            CopiesFrameBeforeReturning,
            AppliesBackpressureWithoutDroppingFrames,
            FinishWaitsForAllFramesAndFlushesOnce,
            WorkerFailureUnblocksProducerAndReleasesBuffers,
            WorkerFailureTakesPrecedenceOverFlushFailure,
            FlushFailureReleasesBuffers,
            AllocationFailureReleasesPartialPool,
            InvalidFrameSizeDoesNotReachEncoder,
            AllPixelFormatsPreservePayload,
            DisposeBeforeStartIsSafe,
            OwnedLeaseStaysAliveUntilConsumed,
            WorkerFailureReleasesPendingOwnedLeases,
            RejectedOwnedLeaseIsReleased,
            DefaultSoftwareAndNvencSettings,
            HardwareOptionsUseBackendSpecificFlags,
            BitrateControlIsExplicit,
            UnsupportedOptionsFailEarly,
            InvalidRateControlCombinationFailsEarly
        ];

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ORV_FFMPEG")))
        {
            tests =
            [
                ..tests,
                RealFFmpegPreservesRGBFrames,
                RealFFmpegPreservesYUV420Frames,
                RealFFmpegPreservesYUV444Frames,
                RealFFmpegPreservesNV12Frames,
                RealFFmpegFailureIsReportedAndProcessIsReleased,
                RealFFmpegMuxesAudioAndVideo,
                RealFFmpegMuxesProductionFrameRate,
                RealFFmpegMuxesHighFpsAudioAndVideo,
                RealFFmpegMuxesShortAudioAndVideo,
                RealFFmpegMuxFailureCancelsBothInputs,
                RealFFmpegMuxPreservesMarkerTiming,
                RealFFmpegRejectsUnknownEncoderBeforeRecording
            ];
        }
        else
        {
            Console.WriteLine("SKIP real FFmpeg checks: set ORV_FFMPEG to the FFmpeg executable");
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ORV_HARDWARE_ENCODER")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ORV_FFMPEG")))
            tests = [..tests, RealHardwareEncoderProductionProfile];
        else
            Console.WriteLine("SKIP hardware production-profile check: set ORV_FFMPEG and ORV_HARDWARE_ENCODER");

        foreach (var test in tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS {test.Method.Name}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL {test.Method.Name}: {exception}");
                return 1;
            }
        }

        Console.WriteLine($"{tests.Length} tests passed");
        return 0;
    }

    private static void CopiesFrameBeforeReturning()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var inner = new TestEncoder { BeforeWrite = () => { entered.Set(); Wait(release); } };
        var pool = new TrackingPool();
        var queued = new QueuedEncoder(inner, 2, pool);
        queued.Start();
        try
        {
            byte[] frame = [1, 2, 3];
            queued.WriteFrame(frame);
            Wait(entered);
            Array.Fill(frame, (byte)99);
        }
        finally
        {
            release.Set();
            queued.Dispose();
        }

        Assert(inner.Frames.Single().SequenceEqual(new byte[] { 1, 2, 3 }), "The producer's data was not copied");
        pool.AssertReturned(pool.Rented);
    }

    private static void AppliesBackpressureWithoutDroppingFrames()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var producerStarted = new ManualResetEventSlim();
        var inner = new TestEncoder { BeforeWrite = () => { entered.Set(); Wait(release); } };
        var pool = new TrackingPool();
        var queued = new QueuedEncoder(inner, 2, pool);
        queued.Start();
        Task producer = null;
        try
        {
            queued.WriteFrame(new byte[] { 1, 0, 0 });
            Wait(entered);
            queued.WriteFrame(new byte[] { 2, 0, 0 });
            producer = Task.Run(() =>
            {
                producerStarted.Set();
                queued.WriteFrame(new byte[] { 3, 0, 0 });
            });
            Wait(producerStarted);
            Assert(!producer.Wait(100), "A full queue must block its producer");
            Assert(pool.Rented == 2, "Backpressure must not allocate another frame buffer");
        }
        finally
        {
            release.Set();
            if (producer != null) Wait(producer);
            queued.Dispose();
        }

        Assert(inner.Frames.Select(frame => frame[0]).SequenceEqual(new byte[] { 1, 2, 3 }), "Frames were reordered or dropped");
        pool.AssertReturned(pool.Rented);
    }

    private static void FinishWaitsForAllFramesAndFlushesOnce()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finishStarted = new ManualResetEventSlim();
        var inner = new TestEncoder { BeforeWrite = () => { entered.Set(); Wait(release); } };
        var queued = new QueuedEncoder(inner);
        queued.Start();
        Task finish = null;
        try
        {
            queued.WriteFrame(new byte[] { 1, 0, 0 });
            Wait(entered);
            queued.WriteFrame(new byte[] { 2, 0, 0 });
            finish = Task.Run(() => { finishStarted.Set(); queued.Finish(); });
            Wait(finishStarted);
            Assert(!finish.Wait(100), "Finish returned before the in-flight frame completed");
        }
        finally
        {
            release.Set();
            if (finish != null) Wait(finish);
            queued.Dispose();
        }

        Assert(inner.FramesAtFinish == 2 && inner.FinishCount == 1, "The encoder was not flushed exactly once after draining");
        Assert(!queued.CanWrite, "The completed queue still accepts frames");
    }

    private static void WorkerFailureUnblocksProducerAndReleasesBuffers()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var producerStarted = new ManualResetEventSlim();
        var failure = new InvalidOperationException("encoding failed");
        var inner = new TestEncoder { BeforeWrite = () => { entered.Set(); Wait(release); throw failure; } };
        var pool = new TrackingPool();
        var queued = new QueuedEncoder(inner, 1, pool);
        queued.Start();
        queued.WriteFrame(new byte[] { 1, 0, 0 });
        Wait(entered);
        var producer = Task.Run(() =>
        {
            producerStarted.Set();
            return Catch(() => queued.WriteFrame(new byte[] { 2, 0, 0 }));
        });
        try
        {
            Wait(producerStarted);
            Assert(!producer.Wait(100), "The producer was expected to wait for a buffer");
        }
        finally
        {
            release.Set();
        }

        Wait(producer);
        Assert(ReferenceEquals(producer.Result, failure), "The waiting producer lost the encoder failure");
        Assert(ReferenceEquals(Catch(() => _ = queued.CanWrite), failure), "CanWrite hid the background failure");
        Assert(ReferenceEquals(Catch(queued.Finish), failure), "Finish hid the background failure");
        queued.Dispose();
        Assert(inner.FinishCount == 1, "The failed encoder was not closed");
        pool.AssertReturned(1);
    }

    private static void WorkerFailureTakesPrecedenceOverFlushFailure()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var failure = new InvalidOperationException("encoding failed");
        var inner = new TestEncoder
        {
            BeforeWrite = () => { entered.Set(); Wait(release); throw failure; },
            FinishFailure = new Exception("flush failed")
        };
        var pool = new TrackingPool();
        var queued = new QueuedEncoder(inner, 3, pool);
        queued.Start();
        queued.WriteFrame(new byte[] { 1, 2, 3 });
        try
        {
            Wait(entered);
            queued.WriteFrame(new byte[] { 4, 5, 6 });
            queued.WriteFrame(new byte[] { 7, 8, 9 });
        }
        finally
        {
            release.Set();
        }
        Assert(ReferenceEquals(Catch(queued.Finish), failure), "Flush replaced the original encoding failure");
        queued.Dispose();
        pool.AssertReturned(pool.Rented);
    }

    private static void FlushFailureReleasesBuffers()
    {
        var failure = new InvalidOperationException("flush failed");
        var inner = new TestEncoder { FinishFailure = failure };
        var pool = new TrackingPool();
        var queued = new QueuedEncoder(inner, 3, pool);
        queued.Start();
        queued.WriteFrame(new byte[] { 1, 2, 3 });
        Assert(ReferenceEquals(Catch(queued.Finish), failure), "The flush failure was hidden");
        queued.Dispose();
        pool.AssertReturned(pool.Rented);
    }

    private static void AllocationFailureReleasesPartialPool()
    {
        var failure = new InvalidOperationException("pool allocation failed");
        var pool = new TrackingPool { RentFailure = failure, FailAfter = 1 };
        var inner = new TestEncoder();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        inner.BeforeWrite = () => { entered.Set(); Wait(release); };
        using var queued = new QueuedEncoder(inner, 3, pool);
        queued.Start();
        queued.WriteFrame(new byte[] { 1, 2, 3 });
        try
        {
            Wait(entered);
            Assert(ReferenceEquals(Catch(() => queued.WriteFrame(new byte[] { 4, 5, 6 })), failure), "Allocation lost its original failure");
        }
        finally { release.Set(); queued.Finish(); }
        Assert(inner.FinishCount == 1, "The encoder was not closed after allocation failed");
        pool.AssertReturned(1);
    }

    private static void InvalidFrameSizeDoesNotReachEncoder()
    {
        var inner = new TestEncoder();
        using var queued = new QueuedEncoder(inner);
        queued.Start();
        Assert(Catch(() => queued.WriteFrame(new byte[] { 1 })) is ArgumentException, "An invalid frame was accepted");
        queued.Finish();
        Assert(inner.Frames.Count == 0, "An invalid frame reached the encoder");
    }

    private static void AllPixelFormatsPreservePayload()
    {
        foreach (var format in Enum.GetValues<PixelFormatMode>())
        {
            var inner = new TestEncoder(format, new Size(4, 2));
            using var queued = new QueuedEncoder(inner);
            queued.Start();
            int size = format is PixelFormatMode.RGB or PixelFormatMode.YUV444 ? 24 : 12;
            byte[] frame = Enumerable.Range(0, size).Select(value => (byte)value).ToArray();
            queued.WriteFrame(frame);
            queued.Finish();
            Assert(inner.Frames.Single().SequenceEqual(frame), $"The {format} payload changed");
        }
    }

    private static void DisposeBeforeStartIsSafe()
    {
        var inner = new TestEncoder();
        var queued = new QueuedEncoder(inner);
        queued.Dispose();
        queued.Dispose();
        Assert(inner.FinishCount == 0, "An encoder that never started was flushed");
    }

    private static void OwnedLeaseStaysAliveUntilConsumed()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var inner = new TestEncoder { BeforeWrite = () => { entered.Set(); Wait(release); } };
        using var queued = new QueuedEncoder(inner);
        queued.Start();
        var lease = new TestLease(new byte[] { 1, 2, 3 });
        queued.WriteFrame(lease);
        try
        {
            Wait(entered);
            Assert(lease.DisposeCount == 0, "The owned frame was released before its write completed");
            lease.Bytes[0] = 9;
        }
        finally { release.Set(); queued.Finish(); }
        Assert(inner.Frames.Single()[0] == 9, "The owned frame was copied instead of retaining its lease");
        Assert(lease.DisposeCount == 1, "The owned frame was not released exactly once");
    }

    private static void WorkerFailureReleasesPendingOwnedLeases()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var error = new IOException("owned write failed");
        var inner = new TestEncoder { BeforeWrite = () => { entered.Set(); Wait(release); throw error; } };
        using var queued = new QueuedEncoder(inner, 3);
        queued.Start();
        TestLease[] frames = Enumerable.Range(0, 3).Select(i => new TestLease(new byte[] { (byte)i, 2, 3 })).ToArray();
        queued.WriteFrame(frames[0]);
        Wait(entered);
        queued.WriteFrame(frames[1]);
        queued.WriteFrame(frames[2]);
        release.Set();
        Assert(ReferenceEquals(Catch(queued.Finish), error), "The owned-frame worker failure was hidden");
        Assert(frames.All(frame => frame.DisposeCount == 1), "Owned frames leaked on worker failure");
    }

    private static void RejectedOwnedLeaseIsReleased()
    {
        using var queued = new QueuedEncoder(new TestEncoder());
        queued.Start();
        var lease = new TestLease(new byte[1]);
        Assert(Catch(() => queued.WriteFrame(lease)) is ArgumentException, "An invalid owned frame was accepted");
        Assert(lease.DisposeCount == 1, "The rejected owned frame was not released");
    }

    private static void DefaultSoftwareAndNvencSettings()
    {
        var config = RealFFmpegConfig("unused.mp4", PixelFormatMode.RGB);
        config.Preset = "slow";
        config.Bitrate = "100M";
        var software = FFmpegEncoderOptions.Build(config, new FFmpegEncoderCapabilities(new[] { "crf", "preset" }));
        Assert(software.SequenceEqual(new[] { "-crf", "21", "-preset", "slow" }), "Software defaults changed");
        foreach (string name in new[] { "h264_nvenc", "hevc_nvenc" })
        {
            config.Encoder = name;
            var nvenc = FFmpegEncoderOptions.Build(config, new FFmpegEncoderCapabilities(new[] { "rc", "qp", "preset" }));
            Assert(nvenc.SequenceEqual(new[] { "-rc", "constqp", "-qp", "21", "-preset", "slow" }), "NVENC defaults changed");
            var native = FFmpegEncoderOptions.BuildNative(config, option => option is "rc" or "qp" or "preset");
            Assert(native.Count == 3 && native["rc"] == "constqp" && native["qp"] == "21" && native["preset"] == "slow", "Native options differ from Pipe");
        }
    }

    private static void HardwareOptionsUseBackendSpecificFlags()
    {
        var config = RealFFmpegConfig("unused.mp4", PixelFormatMode.RGB);
        config.RateControl = VideoRateControl.ConstantQuality;
        config.Quality = 25;
        foreach (string name in new[] { "h264_qsv", "hevc_qsv", "h264_videotoolbox", "hevc_videotoolbox" })
        {
            config.Encoder = name;
            var arguments = FFmpegEncoderOptions.Build(config, new FFmpegEncoderCapabilities(Array.Empty<string>()));
            Assert(!arguments.Contains("-crf") && !arguments.Contains("-preset"), $"Unsupported flags were passed to {name}");
            Assert(arguments.Contains(name.EndsWith("_qsv") ? "-global_quality" : "-q:v"), $"Quality was not configured for {name}");
        }
        foreach (string name in new[] { "h264_amf", "hevc_amf" })
        {
            config.Encoder = name;
            var arguments = FFmpegEncoderOptions.Build(config, new FFmpegEncoderCapabilities(new[] { "rc", "qvbr_quality_level", "quality" }));
            Assert(arguments.Contains("-qvbr_quality_level") && !arguments.Contains("-crf"), $"Unsupported AMF quality mapping for {name}");
        }
    }

    private static void BitrateControlIsExplicit()
    {
        var config = RealFFmpegConfig("unused.mp4", PixelFormatMode.RGB);
        config.Bitrate = "5M";
        config.RateControl = VideoRateControl.VariableBitrate;
        var arguments = FFmpegEncoderOptions.Build(config, new FFmpegEncoderCapabilities(new[] { "crf", "preset" }));
        Assert(arguments.Contains("-b:v") && arguments.Contains("5M") && !arguments.Contains("-crf"), "Explicit bitrate was ignored");
        config.RateControl = VideoRateControl.ConstantBitrate;
        arguments = FFmpegEncoderOptions.Build(config, new FFmpegEncoderCapabilities(new[] { "crf", "preset" }));
        Assert(arguments.Contains("-minrate") && arguments.Contains("-maxrate") && arguments.Contains("-bufsize"), "CBR limits were not passed");
    }

    private static void UnsupportedOptionsFailEarly()
    {
        var config = RealFFmpegConfig("unused.mp4", PixelFormatMode.RGB);
        config.Encoder = "hevc_nvenc";
        config.RateControl = VideoRateControl.ConstantQuality;
        Assert(Catch(() => FFmpegEncoderOptions.Build(config, new FFmpegEncoderCapabilities(new[] { "rc", "preset" }))) is NotSupportedException,
            "A missing quality option was silently accepted");
    }

    private static void InvalidRateControlCombinationFailsEarly()
    {
        var config = RealFFmpegConfig("unused.mp4", PixelFormatMode.RGB);
        var capabilities = new FFmpegEncoderCapabilities(new[] { "crf", "qp", "preset" });
        config.RateControl = VideoRateControl.VariableBitrate;
        config.Bitrate = "5M";
        config.Quality = 21;
        Assert(Catch(() => FFmpegEncoderOptions.Build(config, capabilities)) is ArgumentException, "Bitrate control silently ignored Quality");
        config.RateControl = VideoRateControl.ConstantQuantizer;
        config.MaxBitrate = "10M";
        Assert(Catch(() => FFmpegEncoderOptions.Build(config, capabilities)) is ArgumentException, "CQP accepted an ineffective bitrate limit");
        config.MaxBitrate = null;
        config.Quality = 21.5;
        Assert(Catch(() => FFmpegEncoderOptions.Build(config, capabilities)) is ArgumentException, "Integer QP accepted a fractional value");
    }

    private static void RealFFmpegRejectsUnknownEncoderBeforeRecording()
    {
        var config = RealFFmpegConfig(Path.Combine(Path.GetTempPath(), "unused.mp4"), PixelFormatMode.RGB);
        config.Encoder = "orv_nonexistent_encoder";
        using var queued = new QueuedEncoder(new ExternalFFmpegEncoder(config));
        Assert(Catch(queued.Start) is NotSupportedException, "An unavailable encoder was not rejected before recording");
    }

    private static void RealFFmpegMuxesAudioAndVideo() => RealFFmpegMuxRoundTrip(16, 16, 30, 30);
    private static void RealFFmpegMuxesProductionFrameRate() => RealFFmpegMuxRoundTrip(32, 32, 60, 120);
    private static void RealFFmpegMuxesHighFpsAudioAndVideo() => RealFFmpegMuxRoundTrip(1280, 720, 300, 12);
    private static void RealFFmpegMuxesShortAudioAndVideo() => RealFFmpegMuxRoundTrip(16, 16, 30, 1);

    private static void RealFFmpegMuxRoundTrip(int width, int height, int fps, int frameCount)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"orv audio video {Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var config = RealFFmpegConfig(Path.Combine(directory, "clip with spaces.mp4"), PixelFormatMode.RGB);
            config.Resolution = new Size(width, height);
            config.FPS = fps;
            var inner = new ExternalFFmpegEncoder(config);
            using var queued = new QueuedEncoder(inner);
            queued.ConfigureAudio(44100, 2);
            var work = Task.Run(() =>
            {
                queued.Start();
                byte[] frame = new byte[width * height * 3];
                int sampleFrames = 44100 / fps;
                byte[] pcm = new byte[sampleFrames * 4];
                for (int i = 0; i < frameCount; i++)
                {
                    Array.Fill(frame, (byte)(i * 7));
                    for (int sample = 0; sample < sampleFrames; sample++)
                    {
                        short value = (short)(12000 * Math.Sin(2 * Math.PI * 440 * (i * sampleFrames + sample) / 44100));
                        pcm[sample * 4] = pcm[sample * 4 + 2] = (byte)value;
                        pcm[sample * 4 + 1] = pcm[sample * 4 + 3] = (byte)(value >> 8);
                    }
                    queued.WriteFrame(frame);
                    queued.WriteAudio(pcm);
                }
                queued.Finish();
            });
            if (!work.Wait(TimeSpan.FromSeconds(20)))
            {
                GetEncoderProcess(inner)?.Kill(true);
                Catch(() => work.GetAwaiter().GetResult());
                throw new Exception("Audio/video inputs deadlocked");
            }
            work.GetAwaiter().GetResult();
            Assert(Directory.GetFiles(directory).Length == 1, "Muxing created intermediate files");
            Assert(DecodeGray(config.FFmpegExec, config.OutputPath).Length == frameCount * width * height, "Muxed video frame count changed");
            byte[] audio = DecodeAudio(config.FFmpegExec, config.OutputPath);
            int expectedBytes = frameCount * (44100 / fps) * 4;
            Assert(audio.Length >= expectedBytes && audio.Length < expectedBytes + 8192, "Muxed audio has the wrong duration");
            Assert(audio.Any(value => value != 0), "Muxed audio is silent");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void RealFFmpegMuxPreservesMarkerTiming()
    {
        var config = RealFFmpegConfig("unused.mp4", PixelFormatMode.RGB);
        RealFFmpegMarkerRoundTrip(config, 44100, 60);
    }

    private static void RealHardwareEncoderProductionProfile()
    {
        var config = RealFFmpegConfig("unused.mp4", PixelFormatMode.YUV420);
        config.Resolution = new Size(1280, 720);
        config.FPS = 60;
        config.Encoder = Environment.GetEnvironmentVariable("ORV_HARDWARE_ENCODER");
        config.Preset = Environment.GetEnvironmentVariable("ORV_HARDWARE_PRESET") ?? "slow";
        config.Bitrate = "100M";
        RealFFmpegMarkerRoundTrip(config, 48000, 120);
    }

    private static void RealFFmpegMarkerRoundTrip(EncoderConfig config, int sampleRate, int frameCount)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"orv-sync-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            config.OutputPath = Path.Combine(directory, "marker clip.mp4");
            var inner = new ExternalFFmpegEncoder(config);
            using var queued = new QueuedEncoder(inner);
            queued.ConfigureAudio(sampleRate, 2);
            int planeSize = config.Resolution.Width * config.Resolution.Height;
            int markerFrame = frameCount / 2;
            int samplesPerFrame = sampleRate / config.FPS;
            int markerSample = markerFrame * samplesPerFrame;
            void WriteInterval(int index)
            {
                byte[] pcm = new byte[samplesPerFrame * 4];
                for (int sample = 0; sample < samplesPerFrame; sample++)
                {
                    int position = index * samplesPerFrame + sample;
                    short value = position < markerSample ? (short)0 : (short)(12000 * Math.Sin(2 * Math.PI * 440 * position / sampleRate));
                    pcm[sample * 4] = pcm[sample * 4 + 2] = (byte)value;
                    pcm[sample * 4 + 1] = pcm[sample * 4 + 3] = (byte)(value >> 8);
                }
                queued.WriteAudio(pcm);
            }
            var work = Task.Run(() =>
            {
                queued.Start();
                int frameSize = config.PixelFormat == PixelFormatMode.RGB ? planeSize * 3 : planeSize * 3 / 2;
                byte[] frame = new byte[frameSize];
                for (int index = 0; index < frameCount; index++)
                {
                    Array.Fill(frame, config.PixelFormat == PixelFormatMode.RGB ? (byte)0 : (byte)128);
                    byte level = index < markerFrame ? (byte)16 : (byte)235;
                    if (config.PixelFormat == PixelFormatMode.RGB) Array.Fill(frame, level);
                    else Array.Fill(frame, level, 0, planeSize);
                    queued.WriteFrame(frame);
                    if (index > 0) WriteInterval(index - 1);
                }
                WriteInterval(frameCount - 1);
                queued.Finish();
            });
            if (!work.Wait(TimeSpan.FromSeconds(30)))
            {
                GetEncoderProcess(inner)?.Kill(true);
                Catch(() => work.GetAwaiter().GetResult());
                throw new Exception("Marker recording timed out");
            }
            work.GetAwaiter().GetResult();
            byte[] video = DecodeGray(config.FFmpegExec, config.OutputPath);
            Assert(video.Length == frameCount * planeSize, "The marker clip lost video frames");
            int firstBright = Enumerable.Range(0, frameCount).First(index => video[index * planeSize] > 128);
            Assert(firstBright == markerFrame, "The video marker moved to another frame");
            byte[] audio = DecodeAudioAtRate(config.FFmpegExec, config.OutputPath, sampleRate);
            int expectedBytes = frameCount * samplesPerFrame * 4;
            Assert(audio.Length >= expectedBytes && audio.Length < expectedBytes + 8192, "The audio tail or duration changed");
            int onset = -1;
            for (int offset = 0; offset + 256 <= audio.Length / 4; offset += 64)
            {
                double energy = 0;
                for (int sample = offset; sample < offset + 256; sample++)
                {
                    short value = (short)(audio[sample * 4] | audio[sample * 4 + 1] << 8);
                    energy += (double)value * value;
                }
                if (Math.Sqrt(energy / 256) > 2000) { onset = offset; break; }
            }
            Assert(onset >= 0, "The audio marker is missing");
            double delta = (double)onset / sampleRate - (double)firstBright / config.FPS;
            Assert(Math.Abs(delta) <= 1.0 / config.FPS + 1024.0 / sampleRate, $"Audio/video marker offset is {delta:F4} seconds");
            Assert(Directory.GetFiles(directory).Length == 1, "The marker recording created intermediate files");
            Console.WriteLine($"Marker offset: {delta * 1000:F1}ms ({config.Encoder}, {config.Resolution.Width}x{config.Resolution.Height}, {config.FPS}fps)");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void RealFFmpegMuxFailureCancelsBothInputs()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"orv-mux-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var config = RealFFmpegConfig(Path.Combine(directory, "missing", "clip.mp4"), PixelFormatMode.RGB);
            config.Resolution = new Size(640, 480);
            config.FPS = 300;
            var inner = new ExternalFFmpegEncoder(config);
            var pool = new TrackingPool();
            var queued = new QueuedEncoder(inner, 3, pool);
            queued.ConfigureAudio(44100, 2);
            try
            {
                var work = Task.Run(() => Catch(() =>
                {
                    queued.Start();
                    byte[] frame = new byte[640 * 480 * 3];
                    byte[] pcm = new byte[147 * 4];
                    for (int i = 0; i < 100; i++)
                    {
                        queued.WriteFrame(frame);
                        queued.WriteAudio(pcm);
                    }
                    queued.Finish();
                }));
                if (!work.Wait(TimeSpan.FromSeconds(10)))
                {
                    GetEncoderProcess(inner)?.Kill(true);
                    Wait(work);
                    throw new Exception("FFmpeg mux failure did not unblock its producers");
                }
                Assert(work.Result is IOException, "FFmpeg mux failure was not reported");
                Catch(queued.Finish);
                Assert(GetEncoderProcess(inner) == null, "The failed mux process was not released");
                pool.AssertReturned(pool.Rented);
            }
            finally { Catch(queued.Dispose); }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static byte[] DecodeAudio(string executable, string clip) => DecodeAudioAtRate(executable, clip, 44100);

    private static byte[] DecodeAudioAtRate(string executable, string clip, int sampleRate)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (string argument in new[] { "-v", "error", "-i", clip, "-map", "0:a:0", "-f", "s16le", "-ac", "2", "-ar", sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture), "pipe:1" })
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        var error = process.StandardError.ReadToEndAsync();
        Assert(Task.WaitAll(new Task[] { copy, error }, TimeSpan.FromSeconds(10)) && process.WaitForExit(10000), "Audio decode timed out");
        Assert(process.ExitCode == 0, $"Audio decode failed: {error.Result}");
        return output.ToArray();
    }

    private sealed class TestLease : IFrameLease
    {
        internal readonly byte[] Bytes;
        internal int DisposeCount;
        internal TestLease(byte[] bytes) => Bytes = bytes;
        public ReadOnlySpan<byte> Data => Bytes;
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    private static void RealFFmpegPreservesRGBFrames() => RealFFmpegRoundTrip(PixelFormatMode.RGB);
    private static void RealFFmpegPreservesYUV420Frames() => RealFFmpegRoundTrip(PixelFormatMode.YUV420);
    private static void RealFFmpegPreservesYUV444Frames() => RealFFmpegRoundTrip(PixelFormatMode.YUV444);
    private static void RealFFmpegPreservesNV12Frames() => RealFFmpegRoundTrip(PixelFormatMode.NV12);

    private static void RealFFmpegRoundTrip(PixelFormatMode format)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"orv-encoder-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var config = RealFFmpegConfig(Path.Combine(directory, "clip.mkv"), format);
            var inner = new ExternalFFmpegEncoder(config);
            var pool = new TrackingPool();
            using var queued = new QueuedEncoder(inner, 3, pool);
            queued.Start();
            var process = GetEncoderProcess(inner);
            const int frameCount = 7;
            int planeSize = config.Resolution.Width * config.Resolution.Height;
            int frameSize = format is PixelFormatMode.RGB or PixelFormatMode.YUV444 ? planeSize * 3 : planeSize * 3 / 2;
            byte[] frame = new byte[frameSize];
            for (int index = 0; index < frameCount; index++)
            {
                byte level = (byte)(25 + index * 28);
                Array.Fill(frame, format == PixelFormatMode.RGB ? level : (byte)128);
                if (format != PixelFormatMode.RGB)
                    Array.Fill(frame, level, 0, planeSize);
                queued.WriteFrame(frame);
            }
            queued.Finish();
            pool.AssertReturned(pool.Rented);
            Assert(GetEncoderProcess(inner) == null && Catch(() => _ = process.Handle) is InvalidOperationException,
                "The completed FFmpeg process was not disposed");

            byte[] decoded = DecodeGray(config.FFmpegExec, config.OutputPath);
            Assert(decoded.Length == frameCount * planeSize, $"{format}: incorrect decoded frame count or dimensions");
            double previousLevel = -1;
            for (int index = 0; index < frameCount; index++)
            {
                double level = decoded.Skip(index * planeSize).Take(planeSize).Average(value => (double)value);
                Assert(index == 0 || level > previousLevel + 10,
                    $"{format}: frame {index} level {level} did not exceed previous {previousLevel}");
                previousLevel = level;
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void RealFFmpegFailureIsReportedAndProcessIsReleased()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"orv-encoder-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var config = RealFFmpegConfig(Path.Combine(directory, "clip.mkv"), PixelFormatMode.RGB);
            config.OutputPath = Path.Combine(directory, "missing", "clip.mkv");
            var inner = new ExternalFFmpegEncoder(config);
            var pool = new TrackingPool();
            using var queued = new QueuedEncoder(inner, 3, pool);
            queued.Start();
            var process = GetEncoderProcess(inner);
            queued.WriteFrame(new byte[config.Resolution.Width * config.Resolution.Height * 3]);
            var exception = Catch(queued.Finish);
            Assert(exception is IOException || exception is InvalidOperationException && exception.Message.Contains("FFmpeg exited with code"),
                "A real FFmpeg failure did not reach Finish");
            Assert(GetEncoderProcess(inner) == null && Catch(() => _ = process.Handle) is InvalidOperationException,
                "The failed FFmpeg process was not disposed");
            pool.AssertReturned(pool.Rented);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static EncoderConfig RealFFmpegConfig(string outputPath, PixelFormatMode format) => new()
    {
        FFmpegExec = Environment.GetEnvironmentVariable("ORV_FFMPEG"),
        Resolution = new Size(16, 16),
        FPS = 30,
        OutputPath = outputPath,
        Encoder = "libx264",
        Preset = "ultrafast",
        PixelFormat = format,
        ColorSpace = ColorSpaceMode.BT709
    };

    private static Process GetEncoderProcess(ExternalFFmpegEncoder encoder) =>
        (Process)typeof(ExternalFFmpegEncoder).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(encoder);

    private static byte[] DecodeGray(string ffmpegExecutable, string clip)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegExecutable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (string argument in new[] { "-v", "error", "-i", clip, "-map", "0:v:0", "-vsync", "0", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1" })
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        using var decoded = new MemoryStream();
        var output = process.StandardOutput.BaseStream.CopyToAsync(decoded);
        var error = process.StandardError.ReadToEndAsync();
        if (!Task.WaitAll(new Task[] { output, error }, TimeSpan.FromSeconds(10)) || !process.WaitForExit(10000))
        {
            process.Kill(true);
            throw new Exception("FFmpeg decode timed out");
        }
        Assert(process.ExitCode == 0, $"FFmpeg decode failed: {error.Result}");
        return decoded.ToArray();
    }

    private static Exception Catch(Action action)
    {
        try { action(); return null; }
        catch (Exception exception) { return exception; }
    }

    private static void Wait(ManualResetEventSlim signal) => Assert(signal.Wait(TimeSpan.FromSeconds(5)), "Timed out waiting for a signal");
    private static void Wait(Task task)
    {
        Assert(task.Wait(TimeSpan.FromSeconds(5)), "Timed out waiting for a task");
        task.GetAwaiter().GetResult();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class TestEncoder : EncoderBase
    {
        private bool ready;
        public Action BeforeWrite;
        public Exception FinishFailure;
        public readonly List<byte[]> Frames = new();
        public int FinishCount;
        public int FramesAtFinish;
        public override bool CanWrite => ready;

        public TestEncoder(PixelFormatMode format = PixelFormatMode.RGB, Size? resolution = null)
            : base(new EncoderConfig { PixelFormat = format, Resolution = resolution ?? new Size(1, 1) }) { }

        protected override void _startInternal() => ready = true;
        protected override void _writeFrameInternal(ReadOnlySpan<byte> frame)
        {
            BeforeWrite?.Invoke();
            Frames.Add(frame.ToArray());
        }

        protected override void _finishInternal()
        {
            FinishCount++;
            FramesAtFinish = Frames.Count;
            ready = false;
            if (FinishFailure != null) throw FinishFailure;
        }
    }

    private sealed class TrackingPool : ArrayPool<byte>
    {
        private readonly HashSet<byte[]> outstanding = new();
        public int Rented;
        public int Returned;
        public Exception RentFailure;
        public int FailAfter = int.MaxValue;

        public override byte[] Rent(int minimumLength)
        {
            if (Rented == FailAfter) throw RentFailure;
            var buffer = new byte[minimumLength];
            outstanding.Add(buffer);
            Rented++;
            return buffer;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            Assert(outstanding.Remove(array), "A buffer was returned twice or to the wrong pool");
            Returned++;
        }

        public void AssertReturned(int count) => Assert(Rented == count && Returned == count && outstanding.Count == 0, "Pooled buffers leaked");
    }
}
