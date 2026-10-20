using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace osu_replay_renderer_netcore.Audio.Conversion
{
    internal static class DynamicMusicRenderer
    {
        private const int blockFrames = 4096;
        private const int commandFrames = 256;
        private const double preRollSeconds = .25;
        private const double tailSeconds = .25;

        public static void Render(IPcmSource source, IReadOnlyList<MusicPlaybackSegment> segments,
            long outputSamples, string outputPcmPath, string ffmpegExec)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(segments);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPcmPath);
            if (source.Format.SampleRate <= 0 || source.Format.Channels <= 0 || outputSamples < 0)
                throw new ArgumentOutOfRangeException(nameof(outputSamples));
            long outputBytes = checked(outputSamples * source.Format.Channels * sizeof(float));
            ValidateSegments(segments, outputSamples);

            try
            {
                using var output = new FileStream(outputPcmPath, FileMode.Create, FileAccess.Write);
                output.SetLength(outputBytes);
                var buffer = new float[checked(blockFrames * source.Format.Channels)];
                for (int first = 0; first < segments.Count;)
                {
                    if (segments[first].OutputStartSample == segments[first].OutputEndSample)
                    {
                        first++;
                        continue;
                    }

                    int end = first + 1;
                    bool requiresTempo = segments[first].Tempo != 1;
                    while (end < segments.Count && IsContinuous(segments[end - 1], segments[end], source.Format.SampleRate))
                    {
                        requiresTempo |= segments[end].Tempo != 1;
                        end++;
                    }

                    if (requiresTempo)
                    {
                        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegExec);
                        RenderTempoRun(source, segments, first, end, output, buffer, ffmpegExec);
                    }
                    else
                    {
                        for (int index = first; index < end; index++)
                            RenderResampledSegment(source, segments[index], output, buffer);
                    }
                    first = end;
                }
            }
            catch
            {
                File.Delete(outputPcmPath);
                throw;
            }
        }

        private static void ValidateSegments(IReadOnlyList<MusicPlaybackSegment> segments, long outputSamples)
        {
            long previousEnd = 0;
            foreach (var segment in segments)
            {
                if (segment.OutputStartSample < previousEnd || segment.OutputEndSample < segment.OutputStartSample ||
                    segment.OutputEndSample > outputSamples || !double.IsFinite(segment.SourceStartSeconds) ||
                    !double.IsFinite(segment.Frequency) || segment.Frequency <= 0 ||
                    !double.IsFinite(segment.Tempo) || segment.Tempo <= 0 ||
                    !double.IsFinite(segment.Frequency * segment.Tempo) || segment.Frequency * segment.Tempo <= 0)
                    throw new ArgumentException("Music segments must have ordered non-overlapping output ranges and finite positive rates", nameof(segments));
                previousEnd = segment.OutputEndSample;
            }
        }

        internal static bool IsContinuous(MusicPlaybackSegment previous, MusicPlaybackSegment next, int sampleRate)
        {
            if (previous.OutputEndSample != next.OutputStartSample) return false;
            double expectedSource = previous.SourceStartSeconds +
                (previous.OutputEndSample - previous.OutputStartSample) / (double)sampleRate * previous.Frequency * previous.Tempo;
            double tolerance = Math.Max(previous.Frequency * previous.Tempo, next.Frequency * next.Tempo) / sampleRate;
            return Math.Abs(expectedSource - next.SourceStartSeconds) <= tolerance + 1e-9;
        }

        private static void RenderResampledSegment(IPcmSource source, MusicPlaybackSegment segment, Stream output, float[] buffer)
        {
            int channels = source.Format.Channels;
            output.Position = checked(segment.OutputStartSample * channels * sizeof(float));
            long count = segment.OutputEndSample - segment.OutputStartSample;
            double sourceStart = segment.SourceStartSeconds * source.Format.SampleRate;
            for (long written = 0; written < count;)
            {
                int frames = (int)Math.Min(blockFrames, count - written);
                var values = buffer.AsSpan(0, frames * channels);
                BandLimitedPcmResampler.Read(source, sourceStart + written * segment.Frequency, segment.Frequency, values);
                WriteFloatPcm(output, values);
                written += frames;
            }
        }

        private static void RenderTempoRun(IPcmSource source, IReadOnlyList<MusicPlaybackSegment> segments,
            int first, int end, Stream output, float[] buffer, string ffmpegExec)
        {
            string directory = Path.Combine(Path.GetTempPath(), $"orv-dynamic-music-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var initial = segments[first];
                var last = segments[end - 1];
                int sampleRate = source.Format.SampleRate;
                int channels = source.Format.Channels;
                long wantedFrames = last.OutputEndSample - initial.OutputStartSample;
                int preRollFrames = (int)Math.Ceiling(preRollSeconds * sampleRate);
                double initialRate = initial.Frequency * initial.Tempo;
                long prefixOutputFrames = checked((long)Math.Round(preRollFrames / initialRate));
                double sourceEnd = last.SourceStartSeconds +
                    (last.OutputEndSample - last.OutputStartSample) / (double)sampleRate * last.Frequency * last.Tempo;
                double sourceFrames = (sourceEnd - initial.SourceStartSeconds + tailSeconds) * sampleRate;
                if (!double.IsFinite(sourceFrames) || sourceFrames > long.MaxValue - preRollFrames || sourceFrames < 0)
                    throw new ArgumentOutOfRangeException(nameof(segments), "Music run is too long");
                long inputFrames = checked((long)Math.Ceiling(sourceFrames));
                using (var input = File.Create(Path.Combine(directory, "source.pcm")))
                {
                    Array.Clear(buffer);
                    WriteSilence(input, preRollFrames, channels, buffer);
                    for (long written = 0; written < inputFrames;)
                    {
                        int frames = (int)Math.Min(blockFrames, inputFrames - written);
                        var values = buffer.AsSpan(0, frames * channels);
                        BandLimitedPcmResampler.Read(source, initial.SourceStartSeconds * sampleRate + written, 1, values);
                        WriteFloatPcm(input, values);
                        written += frames;
                    }
                }

                File.WriteAllText(Path.Combine(directory, "commands.txt"), BuildCommands(segments, first, end, preRollFrames / (double)sampleRate, sampleRate), new UTF8Encoding(false));
                string filter = FormattableString.Invariant($"asetnsamples=n={commandFrames}:p=0,asendcmd=f=commands.txt,rubberband@music=tempo={initialRate:R}:pitch={initial.Frequency:R}:window=short,atrim=start_sample={prefixOutputFrames}:end_sample={checked(prefixOutputFrames + wantedFrames)},apad=whole_len={wantedFrames},atrim=end_sample={wantedFrames}");
                long wantedBytes = checked(wantedFrames * channels * sizeof(float));
                RunFFmpeg(ffmpegExec, directory, sampleRate, channels, filter, wantedBytes, rendered =>
                {
                    CopyRenderedRun(source.Length, sampleRate, channels, segments, first, end, rendered, output, buffer);
                });
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        internal static void CopyRenderedRun(long sourceLength, int sampleRate, int channels,
            IReadOnlyList<MusicPlaybackSegment> segments, int first, int end, Stream rendered, Stream output, float[] buffer)
        {
            for (int index = first; index < end; index++)
            {
                var segment = segments[index];
                long count = segment.OutputEndSample - segment.OutputStartSample;
                double start = segment.SourceStartSeconds * sampleRate;
                double rate = segment.Frequency * segment.Tempo;
                long validStart = ClampCeiling(-start / rate, count);
                long validEnd = ClampCeiling((sourceLength - start) / rate, count);
                if (validEnd <= validStart)
                {
                    CopyPcm(rendered, null, checked(count * channels * sizeof(float)), buffer);
                    continue;
                }
                CopyPcm(rendered, null, checked(validStart * channels * sizeof(float)), buffer);
                output.Position = checked((segment.OutputStartSample + validStart) * channels * sizeof(float));
                CopyPcm(rendered, output, checked((validEnd - validStart) * channels * sizeof(float)), buffer);
                CopyPcm(rendered, null, checked((count - validEnd) * channels * sizeof(float)), buffer);
            }
        }

        internal static string BuildCommands(IReadOnlyList<MusicPlaybackSegment> segments, int first, int end, double prefixSeconds, int sampleRate)
        {
            var commands = new StringBuilder();
            var initial = segments[first];
            var previous = initial;
            double commandWindow = 2d * commandFrames / sampleRate + .000002;
            for (int index = first + 1; index < end; index++)
            {
                var segment = segments[index];
                if (segment.Frequency == previous.Frequency && segment.Tempo == previous.Tempo) continue;
                double time = prefixSeconds + segment.SourceStartSeconds - initial.SourceStartSeconds;
                commands.Append(time.ToString("R", CultureInfo.InvariantCulture));
                // Keep every command deliverable at the next input block, then expire its interval
                commands.Append('-');
                commands.Append((time + commandWindow).ToString("R", CultureInfo.InvariantCulture));
                commands.Append(" rubberband@music tempo ");
                commands.Append((segment.Frequency * segment.Tempo).ToString("R", CultureInfo.InvariantCulture));
                commands.Append(", rubberband@music pitch ");
                commands.Append(segment.Frequency.ToString("R", CultureInfo.InvariantCulture));
                commands.AppendLine(";");
                previous = segment;
            }
            // asendcmd requires at least one command even for a constant-rate run
            if (commands.Length == 0)
                commands.Append(FormattableString.Invariant($"0 rubberband@music tempo {initial.Frequency * initial.Tempo:R};\n"));
            return commands.ToString();
        }

        private static void RunFFmpeg(string executable, string directory, int sampleRate, int channels,
            string filter, long wantedBytes, Action<Stream> readOutput)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable.Contains('/') || executable.Contains('\\') ? Path.GetFullPath(executable) : executable,
                    WorkingDirectory = directory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            var args = process.StartInfo.ArgumentList;
            foreach (string argument in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "f32le",
                         "-ar", sampleRate.ToString(CultureInfo.InvariantCulture), "-ac", channels.ToString(CultureInfo.InvariantCulture),
                         "-i", "source.pcm", "-af", filter, "-f", "f32le", "-c:a", "pcm_f32le", "pipe:1"
                     })
                args.Add(argument);
            try
            {
                process.Start();
            }
            catch (Win32Exception error)
            {
                throw new InvalidOperationException($"Could not start FFmpeg for dynamic music rendering: {executable}", error);
            }
            var diagnostics = process.StandardError.ReadToEndAsync();
            try
            {
                try
                {
                    readOutput(process.StandardOutput.BaseStream);
                }
                catch (EndOfStreamException error)
                {
                    process.WaitForExit();
                    string failure = diagnostics.GetAwaiter().GetResult();
                    if (process.ExitCode != 0) throw RenderingFailure(process.ExitCode, failure);
                    throw new InvalidDataException($"FFmpeg returned fewer than {wantedBytes} music bytes", error);
                }
                if (process.StandardOutput.BaseStream.ReadByte() != -1)
                    throw new InvalidDataException($"FFmpeg returned more than {wantedBytes} music bytes");
                process.WaitForExit();
                string message = diagnostics.GetAwaiter().GetResult();
                if (process.ExitCode != 0) throw RenderingFailure(process.ExitCode, message);
            }
            catch
            {
                try
                {
                    if (!process.HasExited) process.Kill();
                    process.WaitForExit();
                    diagnostics.GetAwaiter().GetResult();
                }
                catch
                {
                    // Preserve the rendering failure while cleaning up the owned process
                }
                throw;
            }
        }

        private static InvalidOperationException RenderingFailure(int exitCode, string message) =>
            new($"Dynamic music with preserved pitch requires a working FFmpeg rubberband filter (--enable-librubberband). FFmpeg exited with code {exitCode}: {message.Trim()}");

        private static void WriteSilence(Stream output, long frames, int channels, float[] buffer)
        {
            while (frames > 0)
            {
                int count = (int)Math.Min(blockFrames, frames);
                WriteFloatPcm(output, buffer.AsSpan(0, count * channels));
                frames -= count;
            }
        }

        private static long ClampCeiling(double value, long maximum) =>
            value <= 0 ? 0 : value >= maximum ? maximum : (long)Math.Ceiling(value);

        private static void CopyPcm(Stream source, Stream destination, long bytes, float[] buffer)
        {
            var scratch = MemoryMarshal.AsBytes(buffer.AsSpan());
            while (bytes > 0)
            {
                int count = (int)Math.Min(bytes, scratch.Length);
                source.ReadExactly(scratch[..count]);
                destination?.Write(scratch[..count]);
                bytes -= count;
            }
        }

        private static void WriteFloatPcm(Stream output, Span<float> values)
        {
            var bytes = MemoryMarshal.AsBytes(values);
            if (BitConverter.IsLittleEndian)
            {
                output.Write(bytes);
                return;
            }
            var integers = MemoryMarshal.Cast<float, int>(values);
            for (int index = 0; index < integers.Length; index++)
                integers[index] = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(integers[index]);
            output.Write(bytes);
        }
    }
}
