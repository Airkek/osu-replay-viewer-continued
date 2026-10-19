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
            StartupFailureReleasesPartialPool,
            InvalidFrameSizeDoesNotReachEncoder,
            AllPixelFormatsPreservePayload,
            DisposeBeforeStartIsSafe
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
                RealFFmpegFailureIsReportedAndProcessIsReleased
            ];
        }
        else
        {
            Console.WriteLine("SKIP real FFmpeg checks: set ORV_FFMPEG to the FFmpeg executable");
        }

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
        pool.AssertReturned(2);
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
        pool.AssertReturned(2);
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
        pool.AssertReturned(3);
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
        pool.AssertReturned(3);
    }

    private static void StartupFailureReleasesPartialPool()
    {
        var failure = new InvalidOperationException("pool allocation failed");
        var pool = new TrackingPool { RentFailure = failure, FailAfter = 1 };
        var inner = new TestEncoder();
        using var queued = new QueuedEncoder(inner, 3, pool);
        Assert(ReferenceEquals(Catch(queued.Start), failure), "Startup lost its original failure");
        Assert(inner.FinishCount == 1, "The encoder was not closed after startup failed");
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
            pool.AssertReturned(3);
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
            config.Encoder = "orv_nonexistent_encoder";
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
            pool.AssertReturned(3);
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
        (Process)typeof(ExternalFFmpegEncoder).GetProperty("FFmpeg", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(encoder);

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
        foreach (string argument in new[] { "-v", "error", "-i", clip, "-map", "0:v:0", "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1" })
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
