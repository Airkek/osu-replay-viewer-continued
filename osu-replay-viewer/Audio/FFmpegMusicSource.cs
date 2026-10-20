using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using osu_replay_renderer_netcore.Audio.Conversion;

namespace osu_replay_renderer_netcore.Audio
{
    internal sealed class FFmpegMusicSource : IPcmSource
    {
        private const int blockFrames = 16384;
        private const int cacheBlocks = 3;
        private readonly string path;
        private readonly string executable;
        private readonly double volume;
        private readonly CacheBlock[] cache;
        private int nextBlock;
        private Process decoder;
        private Task<string> diagnostics;
        private long decoderStart;
        private long decoderFrame;
        private long remainingBytes = long.MaxValue;
        private long emptyFrom = long.MaxValue;
        private bool disposed;

        public AudioFormat Format { get; }
        public long Length { get; private set; } = long.MaxValue;
        internal long DecodedFrames { get; private set; }
        internal int DecoderStarts { get; private set; }
        internal bool DecoderRunning => decoder != null && !decoder.HasExited;
        internal int BufferedFrames
        {
            get
            {
                int frames = 0;
                foreach (var block in cache) frames += block.Frames;
                return frames;
            }
        }

        private sealed class CacheBlock
        {
            public long Start = -1;
            public int Frames;
            public readonly float[] Values;

            public CacheBlock(int channels) => Values = new float[checked(blockFrames * channels)];
        }

        public FFmpegMusicSource(string path, string ffmpegExec, double volume = 1)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegExec);
            if (!double.IsFinite(volume) || volume < 0) throw new ArgumentOutOfRangeException(nameof(volume));
            this.path = Path.GetFullPath(path);
            executable = ffmpegExec.Contains('/') || ffmpegExec.Contains('\\') ? Path.GetFullPath(ffmpegExec) : ffmpegExec;
            this.volume = volume;
            try
            {
                Format = OpenInitialDecoder();
                cache = new CacheBlock[cacheBlocks];
                for (int index = 0; index < cache.Length; index++) cache[index] = new CacheBlock(Format.Channels);
            }
            catch
            {
                try { StopDecoder(); }
                catch (Exception cleanup) when (cleanup is IOException or Win32Exception or InvalidOperationException) { }
                throw;
            }
        }

        public void ReadFrames(long firstFrame, Span<float> output)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            int channels = Format.Channels;
            if (output.Length % channels != 0) throw new ArgumentException("PCM output must contain complete channel frames", nameof(output));
            output.Clear();
            int frames = output.Length / channels;
            int destinationFrame = 0;
            if (firstFrame < 0)
            {
                int skip = firstFrame <= -frames ? frames : (int)-firstFrame;
                firstFrame += skip;
                destinationFrame += skip;
            }

            try
            {
                if (decoder != null && decoder.HasExited && decoder.ExitCode != 0)
                    StopDecoder(reportFailure: true);
                while (destinationFrame < frames && firstFrame < Math.Min(Length, emptyFrom))
                {
                    CacheBlock block = FindBlock(firstFrame);
                    if (block == null)
                    {
                        long start = firstFrame - firstFrame % blockFrames;
                        if (decoder == null || decoderFrame != start) StartDecoder(start);
                        block = ReadBlock(start);
                        if (block.Frames == 0) break;
                    }
                    int offset = checked((int)(firstFrame - block.Start));
                    int count = Math.Min(frames - destinationFrame, block.Frames - offset);
                    if (count <= 0) break;
                    block.Values.AsSpan(offset * channels, count * channels)
                        .CopyTo(output.Slice(destinationFrame * channels));
                    firstFrame += count;
                    destinationFrame += count;
                }
            }
            catch
            {
                try { StopDecoder(); }
                catch (Exception cleanup) when (cleanup is IOException or Win32Exception or InvalidOperationException) { }
                throw;
            }
        }

        private CacheBlock FindBlock(long position)
        {
            foreach (var block in cache)
                if (block.Start >= 0 && position >= block.Start && position - block.Start < block.Frames)
                    return block;
            return null;
        }

        private CacheBlock ReadBlock(long start)
        {
            var block = cache[nextBlock];
            block.Frames = 0;
            block.Start = start;
            var bytes = MemoryMarshal.AsBytes(block.Values.AsSpan());
            int requested = (int)Math.Min(bytes.Length, remainingBytes);
            int read = 0;
            while (read < requested)
            {
                int count = decoder.StandardOutput.BaseStream.Read(bytes.Slice(read, requested - read));
                if (count == 0) break;
                read += count;
            }
            int bytesPerFrame = Format.Channels * sizeof(float);
            if (read % bytesPerFrame != 0) throw new InvalidDataException("FFmpeg returned an incomplete float PCM channel frame");
            if (remainingBytes != long.MaxValue)
            {
                if (read != requested) throw new EndOfStreamException("FFmpeg returned truncated WAVE PCM data");
                remainingBytes -= read;
            }
            int frames = read / bytesPerFrame;
            long end = checked(start + frames);
            if (read < bytes.Length || remainingBytes == 0)
            {
                bool exactEnd = frames > 0 || decoderFrame > decoderStart || decoderStart == 0;
                FinishDecoder();
                if (exactEnd) Length = Math.Min(Length, end);
                else emptyFrom = Math.Min(emptyFrom, start);
            }
            decoderFrame = end;
            DecodedFrames += frames;
            if (!BitConverter.IsLittleEndian)
            {
                var integers = MemoryMarshal.Cast<float, int>(block.Values.AsSpan(0, frames * Format.Channels));
                for (int index = 0; index < integers.Length; index++) integers[index] = BinaryPrimitives.ReverseEndianness(integers[index]);
            }
            block.Frames = frames;
            nextBlock = (nextBlock + 1) % cache.Length;
            return block;
        }

        private void StartDecoder(long firstFrame)
        {
            StopDecoder(reportFailure: true);
            var arguments = new List<string>();
            long seekFrame = Math.Max(0, firstFrame - Format.SampleRate);
            if (seekFrame > 0)
                arguments.AddRange(new[] { "-ss", (seekFrame / (double)Format.SampleRate).ToString("R", CultureInfo.InvariantCulture) });
            arguments.AddRange(new[] { "-i", path, "-vn" });
            var filters = new List<string>();
            // Decode a bounded prefix to restore compressed audio filter and bit reservoir state
            if (firstFrame > seekFrame) filters.Add($"atrim=start_sample={firstFrame - seekFrame}");
            if (volume != 1) filters.Add($"volume={volume.ToString("R", CultureInfo.InvariantCulture)}");
            if (filters.Count != 0) arguments.AddRange(new[] { "-af", string.Join(",", filters) });
            arguments.AddRange(new[] { "-f", "f32le", "-c:a", "pcm_f32le", "-ar", Format.SampleRate.ToString(CultureInfo.InvariantCulture),
                "-ac", Format.Channels.ToString(CultureInfo.InvariantCulture), "pipe:1" });
            decoder = StartProcess(arguments);
            diagnostics = DrainDiagnostics(decoder.StandardError);
            decoderStart = decoderFrame = firstFrame;
            remainingBytes = long.MaxValue;
        }

        private AudioFormat OpenInitialDecoder()
        {
            var arguments = new List<string> { "-i", path, "-vn" };
            if (volume != 1) arguments.AddRange(new[] { "-af", $"volume={volume.ToString("R", CultureInfo.InvariantCulture)}" });
            arguments.AddRange(new[] { "-f", "wav", "-c:a", "pcm_f32le", "-ac", "2", "pipe:1" });
            decoder = StartProcess(arguments);
            diagnostics = DrainDiagnostics(decoder.StandardError);
            try
            {
                var header = FloatWaveReader.ReadHeader(decoder.StandardOutput.BaseStream);
                if (header.DataBytes != uint.MaxValue && header.DataBytes % (header.Format.Channels * sizeof(float)) != 0)
                    throw new InvalidDataException("WAVE data must contain complete PCM channel frames");
                remainingBytes = header.DataBytes == uint.MaxValue ? long.MaxValue : header.DataBytes;
                return header.Format;
            }
            catch (Exception error)
            {
                var messages = diagnostics;
                try { StopDecoder(); }
                catch (Exception cleanup) when (cleanup is IOException or Win32Exception or InvalidOperationException) { }
                string message = messages?.GetAwaiter().GetResult() ?? string.Empty;
                throw new InvalidDataException($"Could not read music format: {message.Trim()}", error);
            }
        }

        private Process StartProcess(IEnumerable<string> arguments)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin" }) process.StartInfo.ArgumentList.Add(argument);
            foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            try
            {
                process.Start();
                DecoderStarts++;
                return process;
            }
            catch (Win32Exception error)
            {
                process.Dispose();
                throw new InvalidOperationException($"Could not start FFmpeg for music decoding: {executable}", error);
            }
        }

        private void FinishDecoder()
        {
            var process = decoder;
            decoder = null;
            try
            {
                process.WaitForExit();
                string message = diagnostics.GetAwaiter().GetResult();
                if (process.ExitCode != 0) throw DecodeFailure(process.ExitCode, message);
            }
            finally
            {
                process.Dispose();
                diagnostics = null;
            }
        }

        private void StopDecoder(bool reportFailure = false)
        {
            var process = decoder;
            decoder = null;
            if (process == null) return;
            try
            {
                bool killed = Kill(process);
                string message = diagnostics?.GetAwaiter().GetResult() ?? string.Empty;
                if (reportFailure && !killed && process.ExitCode != 0)
                    throw DecodeFailure(process.ExitCode, message);
            }
            finally
            {
                process.Dispose();
                diagnostics = null;
            }
        }

        private static bool Kill(Process process)
        {
            bool killed = false;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    killed = true;
                }
                process.WaitForExit();
            }
            catch (InvalidOperationException) { }
            return killed;
        }

        private static Task<string> DrainDiagnostics(StreamReader reader) => Task.Run(async () =>
        {
            const int maximum = 8192;
            var tail = new StringBuilder();
            var buffer = new char[1024];
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length)) != 0)
            {
                tail.Append(buffer, 0, count);
                if (tail.Length > maximum) tail.Remove(0, tail.Length - maximum);
            }
            return tail.ToString();
        });

        private static InvalidDataException DecodeFailure(int exitCode, string message) =>
            new($"FFmpeg music decode failed with exit code {exitCode}: {message.Trim()}");

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { StopDecoder(); }
            finally
            {
                foreach (var block in cache)
                {
                    block.Frames = 0;
                    block.Start = -1;
                }
            }
        }
    }
}
