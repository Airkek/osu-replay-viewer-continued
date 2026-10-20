using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace osu_replay_renderer_netcore.Audio
{
    internal delegate void AudioPcmWriter(ReadOnlySpan<byte> pcm);

    internal sealed class RecordedAudioMixer
    {
        private const int chunkSamples = 4096;
        private readonly AudioFormat format;
        private readonly List<VoiceEvent> events = new();
        private readonly object eventsLock = new();
        private int nextVoiceId;

        private enum EventKind
        {
            Start,
            Update,
            Stop
        }

        private readonly record struct VoiceEvent(long Sample, int Order, EventKind Kind, int Id,
            AudioBuffer Buffer = null, double Gain = 1, double Rate = 1, bool Loop = false);

        public RecordedAudioMixer(AudioFormat format)
        {
            ArgumentNullException.ThrowIfNull(format);
            if (format.SampleRate <= 0 || format.Channels <= 0 || format.PCMSize is < 1 or > 4)
                throw new ArgumentOutOfRangeException(nameof(format));
            this.format = format.CreateCopy();
        }

        public int StartVoice(long outputSample, AudioBuffer buffer, double gain, double rate = 1, bool loop = false)
        {
            ValidateSample(outputSample);
            ArgumentNullException.ThrowIfNull(buffer);
            if (buffer.Format.SampleRate <= 0 || buffer.Format.Channels <= 0)
                throw new ArgumentOutOfRangeException(nameof(buffer));
            ValidateGain(gain);
            StreamingAudioMixer.ValidateRate(rate);
            lock (eventsLock)
            {
                int id = checked(++nextVoiceId);
                events.Add(new VoiceEvent(outputSample, events.Count, EventKind.Start, id, buffer, gain, rate, loop));
                return id;
            }
        }

        public void UpdateVoice(long sample, int id, double gain, double rate)
        {
            ValidateSample(sample);
            ValidateGain(gain);
            StreamingAudioMixer.ValidateRate(rate);
            lock (eventsLock)
            {
                ValidateId(id);
                events.Add(new VoiceEvent(sample, events.Count, EventKind.Update, id, Gain: gain, Rate: rate));
            }
        }

        public void StopVoice(long sample, int id)
        {
            ValidateSample(sample);
            lock (eventsLock)
            {
                ValidateId(id);
                events.Add(new VoiceEvent(sample, events.Count, EventKind.Stop, id));
            }
        }

        public void Render(long totalSamples, string musicPcmPath, double musicGain, AudioPcmWriter write)
        {
            ValidateSample(totalSamples);
            ValidateGain(musicGain);
            ArgumentNullException.ThrowIfNull(write);
            VoiceEvent[] orderedEvents;
            lock (eventsLock)
                orderedEvents = events.ToArray();
            Array.Sort(orderedEvents, (a, b) =>
            {
                int order = a.Sample.CompareTo(b.Sample);
                return order != 0 ? order : a.Order.CompareTo(b.Order);
            });

            using var music = musicPcmPath == null ? null : File.OpenRead(musicPcmPath);
            if (music != null && music.Length != checked(totalSamples * format.Channels * sizeof(float)))
                throw new InvalidDataException("The rendered music PCM length does not match the output timeline");
            var musicBuffer = music == null ? null : new float[checked(chunkSamples * format.Channels)];
            var mixer = new StreamingAudioMixer(format);
            var voices = new Dictionary<int, StreamingAudioMixer.ActiveVoice>();
            int eventIndex = 0;
            long position = 0;
            while (position < totalSamples)
            {
                while (eventIndex < orderedEvents.Length && orderedEvents[eventIndex].Sample <= position)
                {
                    var voiceEvent = orderedEvents[eventIndex++];
                    switch (voiceEvent.Kind)
                    {
                        case EventKind.Start:
                            voices.Add(voiceEvent.Id, mixer.AddVoice(voiceEvent.Buffer, voiceEvent.Gain, voiceEvent.Rate, voiceEvent.Loop));
                            break;
                        case EventKind.Update:
                            if (voices.TryGetValue(voiceEvent.Id, out var updatedVoice))
                            {
                                updatedVoice.Gain = voiceEvent.Gain;
                                updatedVoice.Rate = voiceEvent.Rate;
                            }
                            break;
                        case EventKind.Stop:
                            if (voices.Remove(voiceEvent.Id, out var stoppedVoice))
                                stoppedVoice.Stopped = true;
                            break;
                    }
                }

                long nextEvent = eventIndex < orderedEvents.Length ? orderedEvents[eventIndex].Sample : totalSamples;
                int count = (int)Math.Min(chunkSamples, Math.Min(totalSamples, nextEvent) - position);
                ReadOnlySpan<float> musicSamples = ReadOnlySpan<float>.Empty;
                if (music != null)
                {
                    var values = musicBuffer.AsSpan(0, checked(count * format.Channels));
                    music.ReadExactly(MemoryMarshal.AsBytes(values));
                    for (int i = 0; i < values.Length; i++)
                    {
                        if (!BitConverter.IsLittleEndian)
                        {
                            int bits = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(BitConverter.SingleToInt32Bits(values[i]));
                            values[i] = BitConverter.Int32BitsToSingle(bits);
                        }
                        if (!float.IsFinite(values[i]))
                            throw new InvalidDataException("The rendered music PCM contains a non-finite sample");
                    }
                    musicSamples = values;
                }
                write(mixer.MixChunk(count, musicSamples, musicGain));
                position += count;
            }
        }

        private static void ValidateSample(long sample)
        {
            if (sample < 0)
                throw new ArgumentOutOfRangeException(nameof(sample));
        }

        private static void ValidateGain(double gain)
        {
            if (!double.IsFinite(gain))
                throw new ArgumentOutOfRangeException(nameof(gain));
        }

        private void ValidateId(int id)
        {
            if (id <= 0 || id > nextVoiceId)
                throw new ArgumentOutOfRangeException(nameof(id));
        }
    }
}
