using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace osu_replay_renderer_netcore.Audio
{
    /// <summary>
    /// Offline mixer to mix all samples together. You can also mix it with
    /// music.
    /// </summary>
    public class SamplesMixer
    {
        public readonly AudioBuffer Buffer;
        public AudioFormat Format { get => Buffer.Format; }

        public SamplesMixer(AudioBuffer buffer)
        {
            Buffer = buffer;
        }

        public void Mix(AudioBuffer sample, double startSec, double? endSec)
        {
            if (sample == null) 
                return;
            
            var bufferStartSample = (int)Math.Floor(startSec * Format.SampleRate);
            
            int maxSampleCount;
            if (endSec.HasValue)
            {
                var sampleEndSample = (int)Math.Floor(endSec.Value * sample.Format.SampleRate);
                maxSampleCount = sampleEndSample - (int)Math.Floor(startSec * sample.Format.SampleRate);
                if (maxSampleCount < 0)
                    return;
            }
            else
            {
                maxSampleCount = sample.Samples;
            }
            
            maxSampleCount = Math.Min(maxSampleCount, sample.Samples);
            maxSampleCount = Math.Min(maxSampleCount, Buffer.Samples - bufferStartSample);
            if (maxSampleCount <= 0)
                return;
            if (Format.SampleRate == sample.Format.SampleRate)
            {
                for (var i = 0; i < maxSampleCount; i++)
                {
                    for (var ch = 0; ch < Format.Channels; ch++)
                    {
                        Buffer[ch, bufferStartSample + i] += sample[ch, i];
                    }
                }
            }
            else
            {
                var rateRatio = sample.Format.SampleRate / (double)Format.SampleRate;
                for (var i = 0; i < maxSampleCount; i++)
                {
                    for (var ch = 0; ch < Format.Channels; ch++)
                    {
                        Buffer[ch, bufferStartSample + i] += 
                            sample.Resample(ch, Format.SampleRate, i);
                    }
                }
            }
        }

    }
}
