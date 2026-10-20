using SoundTouch;
using System;
using System.IO;
using System.Collections.Generic;

namespace osu_replay_renderer_netcore.Audio
{
    /// <summary>
    /// Audio buffer. Can be the music or the sample. However, the internal sample datas
    /// are represented as float, requiring about 109.9 MiB (115.2 MB) of system memory
    /// for 5 minutes of stereo audio at 48 kHz
    /// </summary>
    public class AudioBuffer : IPcmSource
    {
        /// <summary>
        /// Buffer audio format
        /// </summary>
        public readonly AudioFormat Format;

        /// <summary>
        /// Number of samples in this buffer. Divide by sample rate and you got
        /// the buffer duration.
        /// </summary>
        public int Samples { get; private set; }

        /// <summary>
        /// The buffer duration in seconds
        /// </summary>
        public double Duration { get => ((double)Samples) / Format.SampleRate; }

        private float[] data;
        private readonly float[][] blocks;
        internal const int BlockLength = 16384;

        /// <summary>
        /// Contiguous sample data for hitsounds
        /// </summary>
        public float[] Data
        {
            get => data ?? throw new InvalidOperationException("This audio buffer uses segmented storage");
            private set => data = value;
        }

        private AudioBuffer(AudioFormat format, float[][] blocks, int values)
        {
            Format = format;
            this.blocks = blocks;
            Samples = values / format.Channels;
        }

        internal static AudioBuffer FromBlocks(AudioFormat format, List<float[]> blocks, int values) =>
            new(format, blocks.ToArray(), values);

        public ReadOnlySpan<float> GetReadOnlySpan(int offset, int count)
        {
            int available = Samples * Format.Channels - offset;
            if (offset < 0 || count < 0 || available < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));

            count = Math.Min(count, available);
            if (blocks == null)
                return Data.AsSpan(offset, count);
            if (count == 0)
                return ReadOnlySpan<float>.Empty;

            int blockOffset = offset % BlockLength;
            return blocks[offset / BlockLength].AsSpan(blockOffset, Math.Min(count, BlockLength - blockOffset));
        }

        AudioFormat IPcmSource.Format => Format;
        long IPcmSource.Length => Samples;

        void IPcmSource.ReadFrames(long firstFrame, Span<float> output)
        {
            int channels = Format.Channels;
            if (channels <= 0 || output.Length % channels != 0)
                throw new ArgumentException("PCM output must contain complete channel frames", nameof(output));

            output.Clear();
            int frames = output.Length / channels;
            if (frames == 0 || firstFrame >= Samples || firstFrame <= -(long)frames)
                return;

            int destinationFrame = (int)Math.Max(0, -firstFrame);
            int sourceFrame = (int)Math.Max(0, firstFrame);
            int remaining = checked(Math.Min(frames - destinationFrame, Samples - sourceFrame) * channels);
            int sourceOffset = checked(sourceFrame * channels);
            int destinationOffset = checked(destinationFrame * channels);
            while (remaining > 0)
            {
                var values = GetReadOnlySpan(sourceOffset, remaining);
                values.CopyTo(output.Slice(destinationOffset));
                sourceOffset += values.Length;
                destinationOffset += values.Length;
                remaining -= values.Length;
            }
        }

        void IDisposable.Dispose() { }

        public AudioBuffer(AudioFormat format, int samples)
        {
            Format = format;
            Samples = samples;
            Data = new float[format.Channels * samples];
        }

        /// <summary>
        /// Get the PCM value at index x in channel y
        /// </summary>
        /// <param name="channel">Channel</param>
        /// <param name="index">Sample index</param>
        /// <returns></returns>
        public float this[int channel, int index]
        {
            get {
                int a = Format.Channels * index + channel;
                if (a >= Samples * Format.Channels) return 0f;
                return blocks == null ? Data[a] : blocks[a / BlockLength][a % BlockLength];
            }
            set
            {
                int a = Format.Channels * index + channel;
                if (blocks == null) Data[a] = value;
                else blocks[a / BlockLength][a % BlockLength] = value;
            }
        }

        public float InterpolateAt(double t, int channel)
        {
            int sample = (int)Math.Floor(t * Format.SampleRate);
            float a = sample > 0? this[channel, sample - 1] : this[channel, sample];
            float b = this[channel, sample];
            float c = sample < Samples - 1? this[channel, sample + 1] : this[channel, sample];
            return (a + b * 2f + c) / 4f;
        }

        public float Resample(int channel, int dstSampleRate, int dstIndex)
        {
            double srcIndexFloat = Format.SampleRate * ((double)dstIndex) / dstSampleRate;
            int srcIndex = (int)Math.Floor(srcIndexFloat);
            float a = srcIndex >= 0? this[channel, srcIndex] : this[channel, 0];
            float b = srcIndex < Samples - 2? this[channel, srcIndex + 1] : this[channel, Samples - 1];
            float mix = (float)(srcIndexFloat - srcIndex);
            return a * (1f - mix) + b * mix;
        }

        /// <summary>
        /// Write buffer data as RIFF Wave to stream
        /// </summary>
        /// <param name="stream"></param>
        public void WriteWave(Stream stream)
        {
            stream.Write(new byte[] { 0x52, 0x49, 0x46, 0x46 }); // RIFF
            stream.Write(BitConverter.GetBytes(36 + Samples * Format.Channels * Format.PCMSize)); // int: ChunkSize
            stream.Write(new byte[] { 0x57, 0x41, 0x56, 0x45 }); // WAVE

            stream.Write(new byte[] { 0x66, 0x6d, 0x74, 0x20 }); // "fmt "
            stream.Write(BitConverter.GetBytes(16)); // int: Subchunk1Size
            stream.Write(BitConverter.GetBytes((short)1)); // short: AudioFormat (1 = PCM)
            stream.Write(BitConverter.GetBytes((short)Format.Channels)); // short: NumChannels
            stream.Write(BitConverter.GetBytes(Format.SampleRate)); // int: SampleRate
            stream.Write(BitConverter.GetBytes(Format.SampleRate * Format.Channels * Format.PCMSize)); // int: ByteRate
            stream.Write(BitConverter.GetBytes((short)(Format.Channels * Format.PCMSize))); // short: BlockAlign
            stream.Write(BitConverter.GetBytes((short)(Format.PCMSize * 8))); // short: BitsPerSample

            stream.Write(new byte[] { 0x64, 0x61, 0x74, 0x61 }); // "data"
            stream.Write(BitConverter.GetBytes(Samples * Format.Channels * Format.PCMSize)); // int: Subchunk2Size
            for (int i = 0; i < Samples; i++)
                for (int ch = 0; ch < Format.Channels; ch++) stream.Write(Format.AmpToBytes(this[ch, i]));
        }

        public AudioBuffer Process(Func<double, double> func)
        {
            for (int i = 0; i < Samples; i++)
                for (int ch = 0; ch < Format.Channels; ch++) this[ch, i] = (float)func(this[ch, i]);
            return this;
        }

        public AudioBuffer ScaleTime(double scalePercentage = 1)
        {
            if (scalePercentage == 1) return this;
            var buff2 = new AudioBuffer(Format, (int)Math.Floor(Samples * scalePercentage));
            for (int i = 0; i < buff2.Samples; i++)
            {
                var t = i / (double)buff2.Samples;
                var srcIndex = (int)Math.Floor(t * Samples);
                for (int ch = 0; ch < Format.Channels; ch++) buff2[ch, i] = Resample(ch, Format.SampleRate, srcIndex);
            }
            return buff2;
        }

        public AudioBuffer CreateCopy()
        {
            var formatCopy = Format.CreateCopy();
            var buff2 = new AudioBuffer(formatCopy, Samples);
            int offset = 0;
            while (offset < buff2.Data.Length)
            {
                var source = GetReadOnlySpan(offset, buff2.Data.Length - offset);
                source.CopyTo(buff2.Data.AsSpan(offset));
                offset += source.Length;
            }
            return buff2;
        }

        public AudioBuffer SoundTouchAll(Action<SoundTouchProcessor> apply)
        {
            var p = new SoundTouchProcessor
            {
                Channels = Format.Channels,
                SampleRate = Format.SampleRate
            };
            p.Tempo = 1.0;
            p.Pitch = 1.0;
            p.Rate = 1.0;

            apply(p);
            p.PutSamples(Data, Samples);
            p.Flush();

            var newData = Data;
            if (Math.Abs(p.Pitch - 1) > float.Epsilon ||
                Math.Abs(p.Tempo - 1) > float.Epsilon ||
                Math.Abs(p.Rate - 1) > float.Epsilon)
            {
                newData = new float[Data.Length];
            }
            p.ReceiveSamples(newData, Samples);

            Data = newData;
            return this;
        }

        public static AudioBuffer NoiseBuffer(AudioFormat format, int samples, double amp = 0.78)
        {
            var buff = new AudioBuffer(format, samples);
            Random rng = new();
            for (int i = 0; i < samples; i++)
            {
                for (int ch = 0; ch < format.Channels; ch++)
                    buff[ch, i] = (float)((1.0 - rng.NextDouble() * 2.0) * amp);
            }
            return buff;
        }

        /// <summary>
        /// Create new audio buffer with data generated from given function
        /// </summary>
        /// <param name="format">Audio format to generate</param>
        /// <param name="samples">Number of samples</param>
        /// <param name="func">Generator function, where the given parameter is the
        /// time in second</param>
        /// <returns></returns>
        public static AudioBuffer FromFunction(AudioFormat format, int samples, Func<double, double> func)
        {
            var buff = new AudioBuffer(format, samples);
            double a;
            for (int i = 0; i < samples; i++)
            {
                a = func((double)i / samples);
                for (int ch = 0; ch < format.Channels; ch++) buff[ch, i] = (float)a;
            }
            return buff;
        }

        public static AudioBuffer FromSeconds(AudioFormat format, double seconds) =>
            new(format, (int)Math.Floor(format.SampleRate * seconds));
    }
}
