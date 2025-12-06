using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace osu_replay_renderer_netcore.Audio
{
    public class StreamingAudioMixer
    {
        private readonly List<ActiveVoice> voices = new();
        public readonly AudioFormat Format;

        
        public StreamingAudioMixer(AudioFormat format)
        {
            this.Format = format;
        }

        public class ActiveVoice
        {
            public AudioBuffer Buffer;
            public double Position; // In samples (relative to buffer start)
            public bool Stopped = false;
        }

        public ActiveVoice AddVoice(AudioBuffer buffer)
        {
            var voice = new ActiveVoice { Buffer = buffer, Position = 0 };
            voices.Add(voice);
            return voice;
        }

        public byte[] MixChunk(int sampleCount)
        {
            var mixBuffer = new float[sampleCount * Format.Channels];
            var destSpan = mixBuffer.AsSpan();

            for (int i = voices.Count - 1; i >= 0; i--)
            {
                var voice = voices[i];
                if (voice.Stopped)
                {
                    voices.RemoveAt(i);
                    continue;
                }

                // Calculate how many samples we can take from this voice
                // We need to account for resampling if rates differ
                double rateRatio = voice.Buffer.Format.SampleRate / (double)Format.SampleRate;
                
                // How many output samples we need: sampleCount
                // How many input samples that corresponds to: sampleCount * rateRatio
                
                // Check if voice is finished
                if (voice.Position >= voice.Buffer.Samples)
                {
                    voices.RemoveAt(i);
                    continue;
                }

                if (Math.Abs(rateRatio - 1.0) < double.Epsilon)
                {
                    // Same sample rate - fast path
                    int samplesAvailable = voice.Buffer.Samples - (int)voice.Position;
                    int samplesToMix = Math.Min(sampleCount, samplesAvailable);
                    
                    int channels = Format.Channels;
                    int totalSamples = samplesToMix * channels;
                    int srcOffset = (int)voice.Position * channels;

                    var srcSpan = voice.Buffer.Data.AsSpan(srcOffset, totalSamples);
                    
                    // Vectorized add
                    int v = 0;
                    int vectorSize = Vector<float>.Count;
                    while (v <= totalSamples - vectorSize)
                    {
                        var vDest = new Vector<float>(destSpan.Slice(v));
                        var vSrc = new Vector<float>(srcSpan.Slice(v));
                        (vDest + vSrc).CopyTo(destSpan.Slice(v));
                        v += vectorSize;
                    }
                    for (; v < totalSamples; v++)
                    {
                        destSpan[v] += srcSpan[v];
                    }

                    voice.Position += samplesToMix;
                }
                else
                {
                    // Resampling path
                    for (int j = 0; j < sampleCount; j++)
                    {
                        double srcPos = voice.Position + j * rateRatio;
                        if (srcPos >= voice.Buffer.Samples) break;

                        int srcIndex = (int)srcPos;
                        float mix = (float)(srcPos - srcIndex);

                        for (int ch = 0; ch < Format.Channels; ch++)
                        {
                            int destIdx = j * Format.Channels + ch;
                            
                            float s1 = voice.Buffer[ch, srcIndex];
                            float s2 = (srcIndex + 1 < voice.Buffer.Samples) ? voice.Buffer[ch, srcIndex + 1] : s1;
                            
                            mixBuffer[destIdx] += s1 * (1f - mix) + s2 * mix;
                        }
                    }
                    voice.Position += sampleCount * rateRatio;
                }
            }

            // Apply Tanh and write to stream
            // We can reuse AudioBuffer's writing logic if we wrap this in a temp AudioBuffer
            // But creating AudioBuffer allocates float array. We already have float array.
            // Let's just write directly.
            
            // We need a byte buffer for writing
            int bytesPerSample = Format.PCMSize;
            byte[] byteBuffer = new byte[mixBuffer.Length * bytesPerSample];
            int byteIdx = 0;

            if (bytesPerSample == 2)
            {
                for (int i = 0; i < mixBuffer.Length; i++)
                {
                    float val = (float)Math.Tanh(mixBuffer[i]);
                    short sVal = (short)(val * 32767f);
                    byteBuffer[byteIdx++] = (byte)(sVal & 0xFF);
                    byteBuffer[byteIdx++] = (byte)((sVal >> 8) & 0xFF);
                }
            }
            else
            {
                // Fallback
                for (int i = 0; i < mixBuffer.Length; i++)
                {
                    float val = (float)Math.Tanh(mixBuffer[i]);
                    var bytes = Format.AmpToBytes(val);
                    Array.Copy(bytes, 0, byteBuffer, byteIdx, bytes.Length);
                    byteIdx += bytes.Length;
                }
            }

            return byteBuffer;
        }
    }
}
