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
            var info = ManagedBass.Bass.SampleGetInfo(SampleId);
            
            if (info.Channels < 1) return null;
            
            var format = new AudioFormat
            {
                Channels = info.Channels,
                SampleRate = info.Frequency,
                PCMSize = (int)Math.Ceiling(info.Length / (info.Channels * info.Frequency * (TargetedSample.Length / 1000.0)))
            };

            var samples = info.Length / format.PCMSize / format.Channels;
            var bytes = new byte[info.Length];
            ManagedBass.Bass.SampleGetData(SampleId, bytes);

            var buff = new AudioBuffer(format, samples);
            for (int i = 0; i < samples * format.Channels; i++)
            {
                const float pcm8MaxValue = byte.MaxValue;
                const float pcm16MaxValue = short.MaxValue;
                const float pcm24MaxValue = 1 << 23; // 24 bit signed int max value
                const float pcm32MaxValue = int.MaxValue;
                
                buff.Data[i] = format.PCMSize switch
                {
                    1 => bytes[i] / pcm8MaxValue,
                    2 => BitConverter.ToInt16(bytes, i * format.PCMSize) / pcm16MaxValue,  
                    3 => ((bytes[i * format.PCMSize] & 0xFF) | 
                         ((bytes[i * format.PCMSize + 1] & 0xFF) << 8) | 
                         ((bytes[i * format.PCMSize + 2] & 0xFF) << 16)) / pcm24MaxValue,
                    4 => BitConverter.ToInt32(bytes, i * format.PCMSize) / pcm32MaxValue,
                    _ => 0f
                };
            }
            return buff;
        }
    }
}
