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
        public override bool IsLoaded => (bool)SampleBassFactory.GetMethod("get_IsLoaded").Invoke(factory, null);

        public SampleBassAdapter(ISample sample) : base("test")
        {
            TargetedSample = sample;
            factory = SampleBass.GetField("factory", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(sample);
        }

        protected override SampleChannel CreateChannel() => (SampleChannel)SampleBass.GetMethod("CreateChannel").Invoke(TargetedSample, null);

        public AudioBuffer AsAudioBuffer()
        {
            var info = ManagedBass.Bass.SampleGetInfo(SampleId);
            var format = new AudioFormat
            {
                Channels = info.Channels,
                SampleRate = info.Frequency,
                PCMSize = info.OriginalResolution / 8
            };

            if (format.PCMSize == 0 || format.Channels == 0) return null;

            var samples = info.Length / format.PCMSize / format.Channels;
            var bytes = new byte[info.Length];
            ManagedBass.Bass.SampleGetData(SampleId, bytes);

            var buff = new AudioBuffer(format, samples);
            for (int i = 0; i < samples * format.Channels; i++)
            {
                buff.Data[i] = format.PCMSize switch
                {
                    1 => bytes[i] / 255f,
                    2 => BitConverter.ToInt16(bytes, i * format.PCMSize) / 32768f,
                    _ => 0f
                };
            }
            return buff;
        }
    }
}
