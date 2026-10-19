using System;
using System.Collections.Generic;
using System.Numerics;

namespace osu_replay_renderer_netcore.Audio
{
    public class StreamingAudioMixer
    {
        private readonly List<ActiveVoice> voices = new();
        private readonly List<ActiveVoice> voicesToMix = new();
        private readonly object voicesLock = new();
        private float[] mixBuffer = Array.Empty<float>();
        private byte[] byteBuffer = Array.Empty<byte>();
        public readonly AudioFormat Format;

        public StreamingAudioMixer(AudioFormat format)
        {
            if (format.SampleRate <= 0 || format.Channels <= 0)
                throw new ArgumentOutOfRangeException(nameof(format));
            Format = format;
        }

        public class ActiveVoice
        {
            public AudioBuffer Buffer;
            public double Position; // In samples relative to buffer start
            public bool Stopped;
            public double Gain = 1;
        }

        public ActiveVoice AddVoice(AudioBuffer buffer, double gain = 1)
        {
            var voice = new ActiveVoice { Buffer = buffer, Gain = gain };
            lock (voicesLock)
                voices.Add(voice);
            return voice;
        }

        // The returned span remains valid until the next MixChunk call
        public ReadOnlySpan<byte> MixChunk(int sampleCount)
        {
            if (sampleCount < 0)
                throw new ArgumentOutOfRangeException(nameof(sampleCount));

            int values = checked(sampleCount * Format.Channels);
            int bytes = checked(values * Format.PCMSize);
            if (mixBuffer.Length < values) mixBuffer = new float[values];
            if (byteBuffer.Length < bytes) byteBuffer = new byte[bytes];
            var destSpan = mixBuffer.AsSpan(0, values);
            destSpan.Clear();
            voicesToMix.Clear();

            lock (voicesLock)
            {
                for (int i = voices.Count - 1; i >= 0; i--)
                {
                    var voice = voices[i];
                    if (voice.Stopped || voice.Position >= voice.Buffer.Samples)
                    {
                        voices.RemoveAt(i);
                        continue;
                    }
                    voicesToMix.Add(voice);
                }
            }

            foreach (var voice in voicesToMix)
            {
                double rateRatio = voice.Buffer.Format.SampleRate / (double)Format.SampleRate;
                float gain = (float)voice.Gain;

                if (voice.Buffer.Format.SampleRate == Format.SampleRate &&
                    voice.Buffer.Format.Channels == Format.Channels && voice.Position == Math.Floor(voice.Position))
                {
                    int samplesToMix = Math.Min(sampleCount, voice.Buffer.Samples - (int)voice.Position);
                    int totalValues = samplesToMix * Format.Channels;
                    int srcOffset = (int)voice.Position * Format.Channels;
                    int offset = 0;
                    var vectorGain = new Vector<float>(gain);
                    while (offset < totalValues)
                    {
                        var source = voice.Buffer.GetReadOnlySpan(srcOffset + offset, totalValues - offset);
                        var destination = destSpan.Slice(offset, source.Length);
                        int v = 0;
                        int vectorSize = Vector<float>.Count;
                        while (v <= source.Length - vectorSize)
                        {
                            var vDest = new Vector<float>(destination.Slice(v));
                            var vSrc = new Vector<float>(source.Slice(v));
                            (vDest + vSrc * vectorGain).CopyTo(destination.Slice(v));
                            v += vectorSize;
                        }
                        for (; v < source.Length; v++)
                            destination[v] += source[v] * gain;
                        offset += source.Length;
                    }
                    voice.Position += samplesToMix;
                }
                else
                {
                    for (int j = 0; j < sampleCount; j++)
                    {
                        double srcPos = voice.Position + j * rateRatio;
                        if (srcPos >= voice.Buffer.Samples) break;
                        int srcIndex = (int)srcPos;
                        float fraction = (float)(srcPos - srcIndex);
                        for (int ch = 0; ch < Format.Channels; ch++)
                        {
                            int inputChannel = ch < voice.Buffer.Format.Channels ? ch :
                                voice.Buffer.Format.Channels == 1 ? 0 : -1;
                            if (inputChannel < 0) continue;
                            float first = voice.Buffer[inputChannel, srcIndex];
                            float second = srcIndex + 1 < voice.Buffer.Samples ? voice.Buffer[inputChannel, srcIndex + 1] : first;
                            destSpan[j * Format.Channels + ch] += (first * (1 - fraction) + second * fraction) * gain;
                        }
                    }
                    voice.Position += sampleCount * rateRatio;
                }
            }
            voicesToMix.Clear();

            int byteIndex = 0;
            if (Format.PCMSize == 2)
            {
                for (int i = 0; i < values; i++)
                {
                    short value = (short)((float)Math.Tanh(destSpan[i]) * 32767f);
                    byteBuffer[byteIndex++] = (byte)(value & 0xff);
                    byteBuffer[byteIndex++] = (byte)((value >> 8) & 0xff);
                }
            }
            else
            {
                for (int i = 0; i < values; i++)
                {
                    var encoded = Format.AmpToBytes((float)Math.Tanh(destSpan[i]));
                    encoded.CopyTo(byteBuffer, byteIndex);
                    byteIndex += encoded.Length;
                }
            }
            return byteBuffer.AsSpan(0, bytes);
        }
    }
}
