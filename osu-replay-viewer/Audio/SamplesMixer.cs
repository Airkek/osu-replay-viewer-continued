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

        public void Mix(AudioBuffer sample, double startOffset, double startSec, double? endSec)
        {
            if (sample == null) 
                return;
            
            var bufferStartSample = (int)Math.Floor(startSec * Format.SampleRate);
            
            var sourceStartSample = (int)Math.Floor(startOffset * sample.Format.SampleRate);
            
            if (sourceStartSample < 0) 
                sourceStartSample = 0;
            if (sourceStartSample >= sample.Samples)
                return;
            
            int maxSampleCount;
            if (endSec.HasValue)
            {
                var targetEndSample = (int)Math.Floor(endSec.Value * Format.SampleRate);
                maxSampleCount = targetEndSample - bufferStartSample;
                if (maxSampleCount <= 0)
                    return;
            }
            else
            {
                maxSampleCount = sample.Samples - sourceStartSample; // Доступные сэмплы в исходном буфере
            }
            
            maxSampleCount = Math.Min(maxSampleCount, sample.Samples - sourceStartSample);
            maxSampleCount = Math.Min(maxSampleCount, Buffer.Samples - bufferStartSample);
            
            if (maxSampleCount <= 0)
                return;
            
            if (Format.SampleRate == sample.Format.SampleRate)
            {
                for (var i = 0; i < maxSampleCount; i++)
                {
                    for (var ch = 0; ch < Format.Channels; ch++)
                    {
                        Buffer[ch, bufferStartSample + i] += sample[ch, sourceStartSample + i];
                    }
                }
            }
            else
            {
                var rateRatio = sample.Format.SampleRate / (double)Format.SampleRate;
                for (var i = 0; i < maxSampleCount; i++)
                {
                    var sourceIndex = (int)(sourceStartSample + i * rateRatio);
                    for (var ch = 0; ch < Format.Channels; ch++)
                    {
                        Buffer[ch, bufferStartSample + i] += 
                            sample.Resample(ch, Format.SampleRate, sourceIndex);
                    }
                }
            }
        }
    }
}
