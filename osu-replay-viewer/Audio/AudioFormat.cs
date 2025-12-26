using ManagedBass;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace osu_replay_renderer_netcore.Audio
{
    /// <summary>
    /// Audio format, which stores constants like sample rate and number of channels
    /// </summary>
    public class AudioFormat
    {
        /// <summary>
        /// Sample rate in Hz (or samples per second). A value of 48000 means 48kHz or
        /// 48000 samples per second
        /// </summary>
        public int SampleRate { get; set; } = 48000;

        /// <summary>
        /// The number of channels. The most common value is 2, which is stereo, but
        /// sometimes it's 1 for mono.
        /// </summary>
        public int Channels { get; set; } = 2;

        /// <summary>
        /// PCM value size in bytes. The PCM quality in bits is PCMSize * 8. The default
        /// value is 2, which is PCM 16-bit.
        /// </summary>
        public int PCMSize { get; set; } = 2;

        public int PCMBits { get => PCMSize * 8; }

        /// <summary>
        /// Number of bytes per sample. A sample contains integers for each channel
        /// </summary>
        public int BytesPerSample { get => PCMSize * Channels; }

        public AudioFormat CreateCopy() => new()
        {
            SampleRate = SampleRate,
            Channels = Channels,
            PCMSize = PCMSize
        };

        public WaveFormat ToBass()
        {
            return new WaveFormat(SampleRate, PCMSize * 8, Channels);
        }

        public byte[] AmpToBytes(float amp)
        {
            var clamped = Math.Clamp(amp, -1f, 1f);

            const float pcm8MaxValue = byte.MaxValue;          // unsigned 0..255
            const float pcm16MaxValue = short.MaxValue;        // signed
            const float pcm24MaxValue = 0x7FFFFF;              // 24-bit signed max
            const float pcm32MaxValue = int.MaxValue;          // signed

            switch (PCMSize)
            {
                case 1:
                    // 8-bit PCM is unsigned; map -1..1 to 0..255 with rounding.
                    return [(byte)MathF.Round((clamped * 0.5f + 0.5f) * pcm8MaxValue)];
                case 2:
                    return BitConverter.GetBytes((short)MathF.Round(clamped * pcm16MaxValue));
                case 3:
                    var value24 = (int)MathF.Round(clamped * pcm24MaxValue);
                    return
                    [
                        (byte)(value24 & 0xFF),
                        (byte)((value24 >> 8) & 0xFF),
                        (byte)((value24 >> 16) & 0xFF)
                    ];
                case 4:
                    return BitConverter.GetBytes((int)MathF.Round(clamped * pcm32MaxValue));
                default:
                    return null;
            }
        }

        public override string ToString() => $"AudioFormat({SampleRate}Hz, {Channels} channels, {PCMBits} bits)";
    }
}
