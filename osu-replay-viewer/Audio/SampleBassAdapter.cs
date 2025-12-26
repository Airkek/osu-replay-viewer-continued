using ManagedBass;
using osu.Framework.Audio.Mixing;
using osu.Framework.Audio.Sample;
using System;
using System.Reflection;

namespace osu_replay_renderer_netcore.Audio
{
    public class SampleBassAdapter : Sample
    {
        public static readonly Type SampleBass = typeof(AudioMixer).Assembly.GetType("osu.Framework.Audio.Sample.SampleBass");
        public static readonly Type SampleBassFactory = typeof(AudioMixer).Assembly.GetType("osu.Framework.Audio.Sample.SampleBassFactory");

        public readonly ISample TargetedSample;

        private readonly object factory;

        public int SampleId => (int)SampleBassFactory.GetMethod("get_SampleId").Invoke(factory, null);
        public override double Length => 0;
        public override bool IsLoaded => (bool)SampleBassFactory.GetMethod("get_IsLoaded").Invoke(factory, null);

        public SampleBassAdapter(ISample sample) : base("test")
        {
            TargetedSample = sample;
            factory = SampleBass.GetField("factory", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(sample);
        }

        protected override SampleChannel CreateChannel() => (SampleChannel)SampleBass.GetMethod("CreateChannel").Invoke(TargetedSample, null);

        public AudioBuffer AsAudioBuffer()
        {
            var info = Bass.SampleGetInfo(SampleId);

            if (info.Channels < 1 || info.Length <= 0)
                return null;

            // BASS exposes either OriginalResolution or flags describing the stored sample depth.
            var pcmBits = info.OriginalResolution > 0
                ? info.OriginalResolution
                : info.Flags.HasFlag(BassFlags.Float) ? 32
                : info.Flags.HasFlag(BassFlags.Byte) ? 8
                : 16;

            var format = new AudioFormat
            {
                Channels = info.Channels,
                SampleRate = info.Frequency,
                PCMSize = Math.Max(1, pcmBits / 8)
            };

            var bytesPerFrame = format.PCMSize * format.Channels;
            var samples = info.Length / bytesPerFrame;

            var bytes = new byte[info.Length];
            Bass.SampleGetData(SampleId, bytes);

            var buff = new AudioBuffer(format, samples);
            var isFloat = info.Flags.HasFlag(BassFlags.Float);

            for (int i = 0; i < samples * format.Channels; i++)
            {
                var offset = i * format.PCMSize;

                buff.Data[i] = format.PCMSize switch
                {
                    1 => (bytes[offset] - 128) / 128f, // 8-bit PCM is unsigned
                    2 => BitConverter.ToInt16(bytes, offset) / (float)short.MaxValue,
                    3 => Read24Bit(bytes, offset),
                    4 => isFloat
                        ? BitConverter.ToSingle(bytes, offset)
                        : BitConverter.ToInt32(bytes, offset) / (float)int.MaxValue,
                    _ => 0f
                };
            }

            return buff;

            static float Read24Bit(byte[] buffer, int offset)
            {
                const float pcm24MaxValue = 0x7FFFFF; // (1 << 23) - 1

                var sample = buffer[offset]
                            | (buffer[offset + 1] << 8)
                            | (buffer[offset + 2] << 16);

                // Sign-extend the 24-bit value to 32-bit int
                if ((sample & 0x800000) != 0)
                    sample |= unchecked((int)0xFF000000);

                return sample / pcm24MaxValue;
            }
        }
    }
}
