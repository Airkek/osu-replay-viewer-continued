using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace osu_replay_renderer_netcore.Audio.Conversion
{
    internal sealed class BackgroundMusicRenderer : IDisposable
    {
        private const int blockFrames = 4096;
        private const double preRollSeconds = .25;
        private const double tailSeconds = .25;
        private readonly MusicPlaybackSegment initial;
        private readonly AudioFormat format;
        private readonly Func<CancellationToken, IPcmSource> createSource;
        private readonly string executable;
        private readonly string directory;
        private readonly Func<string, Stream> createOutput;
        private readonly Channel<MusicPlaybackSegment> horizons;
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task worker;
        private MusicPlaybackSegment previous;
        private long observedSegments = 1;
        private long sourceLength;
        private Exception failure;
        private string rejection;
        private bool completed;
        private bool disposed;

        internal bool IsRunning => !worker.IsCompleted;
        internal int MaximumPendingHorizons => 1;
        internal int NativeProcessId { get; private set; }

        internal BackgroundMusicRenderer(AudioFormat format, Func<CancellationToken, IPcmSource> createSource,
            MusicPlaybackSegment initial, string executable, Func<string, Stream> createOutput = null)
        {
            ArgumentNullException.ThrowIfNull(format);
            ArgumentNullException.ThrowIfNull(createSource);
            ArgumentException.ThrowIfNullOrWhiteSpace(executable);
            if (format.SampleRate <= 0 || format.Channels <= 0 || !CanStart(initial))
                throw new ArgumentOutOfRangeException(nameof(initial));
            this.format = format.CreateCopy();
            this.createSource = createSource;
            this.initial = previous = initial;
            this.createOutput = createOutput ?? (path => new FileStream(path, FileMode.Create, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous));
            this.executable = executable.Contains('/') || executable.Contains('\\') ? Path.GetFullPath(executable) : executable;
            directory = Path.Combine(Path.GetTempPath(), $"orv-background-music-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            horizons = Channel.CreateBounded<MusicPlaybackSegment>(new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest
            });
            horizons.Writer.TryWrite(initial);
            worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        internal static bool CanStart(MusicPlaybackSegment segment) =>
            segment.OutputStartSample >= 0 && segment.OutputEndSample > segment.OutputStartSample &&
            double.IsFinite(segment.SourceStartSeconds) && double.IsFinite(segment.Frequency) && segment.Frequency > 0 &&
            double.IsFinite(segment.Tempo) && segment.Tempo > 0 && segment.Tempo != 1 &&
            double.IsFinite(segment.Frequency * segment.Tempo) && segment.Frequency * segment.Tempo > 0;

        internal void Observe(MusicPlaybackSegment segment)
        {
            if (completed || disposed || rejection != null) return;
            if (!Matches(previous, segment))
            {
                Reject("music rates, seek or continuity changed");
                return;
            }
            previous = segment;
            observedSegments++;
            horizons.Writer.TryWrite(segment);
        }

        private bool Matches(MusicPlaybackSegment before, MusicPlaybackSegment after) =>
            after.OutputEndSample > after.OutputStartSample && double.IsFinite(after.SourceStartSeconds) &&
            after.Frequency == initial.Frequency && after.Tempo == initial.Tempo &&
            DynamicMusicRenderer.IsContinuous(before, after, format.SampleRate);

        internal bool TryComplete(IReadOnlyList<MusicPlaybackSegment> actual, long outputSamples,
            string outputPath, out string reason)
        {
            if (disposed || completed) throw new InvalidOperationException("Background music is already closed");
            completed = true;
            if (rejection == null)
            {
                if (actual.Count == 0 || actual.Count != observedSegments || actual[0] != initial ||
                    actual[actual.Count - 1].OutputEndSample > outputSamples)
                    Reject("final music timeline differs from the observed timeline");
                else
                {
                    for (int index = 1; index < actual.Count; index++)
                    {
                        if (!Matches(actual[index - 1], actual[index]))
                        {
                            Reject("final music timeline is not one constant continuous run");
                            break;
                        }
                    }
                }
            }
            if (rejection == null) horizons.Writer.TryWrite(actual[actual.Count - 1]);
            horizons.Writer.TryComplete();
            worker.GetAwaiter().GetResult();
            reason = rejection ?? failure?.Message;
            if (reason != null) return false;

            try
            {
                int channels = format.Channels;
                long wantedFrames = actual[actual.Count - 1].OutputEndSample - initial.OutputStartSample;
                long wantedBytes = checked(wantedFrames * channels * sizeof(float));
                using var rendered = new FileStream(Path.Combine(directory, "rendered.f32"), FileMode.Open, FileAccess.ReadWrite);
                if (rendered.Length % (channels * sizeof(float)) != 0)
                    throw new InvalidDataException("Background music contains an incomplete PCM frame");
                // Match the foreground apad and atrim without restarting the native DSP
                if (rendered.Length < wantedBytes) rendered.SetLength(wantedBytes);
                using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                output.SetLength(checked(outputSamples * channels * sizeof(float)));
                DynamicMusicRenderer.CopyRenderedRun(sourceLength, format.SampleRate, channels, actual, 0, actual.Count,
                    rendered, output, new float[checked(blockFrames * channels)]);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OverflowException)
            {
                reason = error.Message;
                return false;
            }
        }

        private void Reject(string reason)
        {
            rejection = reason;
            cancellation.Cancel();
            horizons.Writer.TryComplete();
        }

        private void Run()
        {
            try
            {
                using var source = createSource(cancellation.Token);
                if (source.Format.SampleRate != format.SampleRate || source.Format.Channels != format.Channels)
                    throw new InvalidDataException("Background source format changed");
                Render(source);
                sourceLength = source.Length;
            }
            catch (Exception error)
            {
                if (!cancellation.IsCancellationRequested) failure = error;
            }
        }

        private void Render(IPcmSource source)
        {
            int sampleRate = format.SampleRate;
            int channels = format.Channels;
            int preRollFrames = (int)Math.Ceiling(preRollSeconds * sampleRate);
            long prefixOutputFrames = checked((long)Math.Round(preRollFrames / (initial.Frequency * initial.Tempo)));
            File.WriteAllText(Path.Combine(directory, "commands.txt"),
                DynamicMusicRenderer.BuildCommands(new[] { initial }, 0, 1, preRollFrames / (double)sampleRate, sampleRate),
                new UTF8Encoding(false));
            string filter = FormattableString.Invariant($"asetnsamples=n=256:p=0,asendcmd=f=commands.txt,rubberband@music=tempo={initial.Frequency * initial.Tempo:R}:pitch={initial.Frequency:R}:window=short,atrim=start_sample={prefixOutputFrames}");
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    WorkingDirectory = directory,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            foreach (string argument in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "f32le",
                         "-ar", sampleRate.ToString(CultureInfo.InvariantCulture), "-ac", channels.ToString(CultureInfo.InvariantCulture),
                         "-i", "pipe:0", "-af", filter, "-f", "f32le", "-c:a", "pcm_f32le", "pipe:1"
                     })
                process.StartInfo.ArgumentList.Add(argument);
            using var rendered = createOutput(Path.Combine(directory, "rendered.f32"));
            process.Start();
            Task output = null;
            Task outputGuard = null;
            Task<string> diagnostics = null;
            try
            {
                NativeProcessId = process.Id;
                using var stop = cancellation.Token.Register(() => StopProcess(process));
                output = process.StandardOutput.BaseStream.CopyToAsync(rendered);
                outputGuard = output.ContinueWith(_ => StopProcess(process), CancellationToken.None,
                    TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                diagnostics = ReadDiagnostics(process.StandardError);
                var buffer = new float[checked(blockFrames * channels)];
                for (int written = 0; written < preRollFrames;)
                {
                    int count = Math.Min(blockFrames, preRollFrames - written);
                    WritePcm(process.StandardInput.BaseStream, buffer.AsSpan(0, count * channels));
                    written += count;
                }
                long inputFrames = 0;
                var last = initial;
                while (horizons.Reader.WaitToReadAsync(cancellation.Token).AsTask().GetAwaiter().GetResult())
                {
                    while (horizons.Reader.TryRead(out var horizon)) last = horizon;
                    long target = InputFrames(last, 0);
                    // Keep the same 4096-frame resampler calls as the foreground source file
                    target -= target % blockFrames;
                    Feed(source, process.StandardInput.BaseStream, buffer, ref inputFrames, target);
                }
                Feed(source, process.StandardInput.BaseStream, buffer, ref inputFrames, InputFrames(last, tailSeconds));
                process.StandardInput.Close();
                output.GetAwaiter().GetResult();
                process.WaitForExit();
                string message = diagnostics.GetAwaiter().GetResult();
                if (process.ExitCode != 0)
                    throw new InvalidDataException($"Background music FFmpeg exited with code {process.ExitCode}: {message.Trim()}");
            }
            catch
            {
                // A failed output consumer can also break stdin after the guard kills FFmpeg
                if (output?.IsFaulted == true || output?.IsCanceled == true) output.GetAwaiter().GetResult();
                throw;
            }
            finally
            {
                StopProcess(process);
                process.WaitForExit();
                try { output?.GetAwaiter().GetResult(); }
                catch (Exception) { }
                try { outputGuard?.GetAwaiter().GetResult(); }
                catch (Exception) { }
                try { diagnostics?.GetAwaiter().GetResult(); }
                catch (Exception) { }
            }
        }

        private long InputFrames(MusicPlaybackSegment last, double tail)
        {
            double sourceEnd = last.SourceStartSeconds +
                (last.OutputEndSample - last.OutputStartSample) / (double)format.SampleRate * last.Frequency * last.Tempo;
            double frames = (sourceEnd - initial.SourceStartSeconds + tail) * format.SampleRate;
            if (!double.IsFinite(frames) || frames < 0 || frames >= long.MaxValue)
                throw new InvalidDataException("Background music run is too long");
            // The final call uses the same ceiling as the foreground source file
            return checked((long)(tail == 0 ? Math.Floor(frames) : Math.Ceiling(frames)));
        }

        private void Feed(IPcmSource source, Stream input, float[] buffer, ref long written, long target)
        {
            while (written < target)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                int frames = (int)Math.Min(blockFrames, target - written);
                var values = buffer.AsSpan(0, frames * format.Channels);
                BandLimitedPcmResampler.Read(source, initial.SourceStartSeconds * format.SampleRate + written, 1, values);
                WritePcm(input, values);
                written += frames;
            }
        }

        private static void WritePcm(Stream output, Span<float> values)
        {
            if (!BitConverter.IsLittleEndian)
            {
                var integers = MemoryMarshal.Cast<float, int>(values);
                for (int index = 0; index < integers.Length; index++)
                    integers[index] = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(integers[index]);
            }
            output.Write(MemoryMarshal.AsBytes(values));
        }

        private static void StopProcess(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
        }

        private static async Task<string> ReadDiagnostics(StreamReader reader)
        {
            const int maximum = 8192;
            var tail = new StringBuilder();
            var buffer = new char[1024];
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
            {
                tail.Append(buffer, 0, count);
                if (tail.Length > maximum) tail.Remove(0, tail.Length - maximum);
            }
            return tail.ToString();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            cancellation.Cancel();
            horizons.Writer.TryComplete();
            worker.GetAwaiter().GetResult();
            cancellation.Dispose();
            Directory.Delete(directory, true);
        }
    }
}
