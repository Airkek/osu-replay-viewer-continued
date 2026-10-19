using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace osu_replay_renderer_netcore.Audio
{
    internal sealed class SamplePcmCache
    {
        private const int maxPitchVariants = 8;
        private const long maxPitchBytes = 8 * 1024 * 1024;
        private readonly ConditionalWeakTable<object, Entry> entries = new();

        private sealed class Entry
        {
            public int SampleId;
            public AudioBuffer Original;
            public long PitchBytes;
            public readonly Dictionary<double, (AudioBuffer Buffer, LinkedListNode<double> Node)> Pitches = new();
            public readonly LinkedList<double> RecentPitches = new();
        }

        public AudioBuffer Get(object owner, int sampleId, double pitch, Func<AudioBuffer> decode)
        {
            if (!double.IsFinite(pitch) || pitch <= 0)
                throw new ArgumentOutOfRangeException(nameof(pitch));

            var entry = entries.GetValue(owner, _ => new Entry());
            lock (entry)
            {
                if (entry.SampleId != sampleId || entry.Original == null)
                {
                    entry.SampleId = sampleId;
                    entry.Original = null;
                    entry.Pitches.Clear();
                    entry.RecentPitches.Clear();
                    entry.PitchBytes = 0;
                    entry.Original = decode();
                }

                if (entry.Original == null || pitch == 1)
                    return entry.Original;

                if (entry.Pitches.TryGetValue(pitch, out var cached))
                {
                    entry.RecentPitches.Remove(cached.Node);
                    entry.RecentPitches.AddLast(cached.Node);
                    return cached.Buffer;
                }

                var buffer = entry.Original.CreateCopy();
                buffer.SoundTouchAll(processor => processor.Pitch = pitch);
                long bytes = (long)buffer.Samples * buffer.Format.Channels * sizeof(float);
                if (bytes > maxPitchBytes)
                    return buffer;

                while (entry.Pitches.Count >= maxPitchVariants || entry.PitchBytes + bytes > maxPitchBytes)
                {
                    double oldest = entry.RecentPitches.First.Value;
                    var removed = entry.Pitches[oldest].Buffer;
                    entry.PitchBytes -= (long)removed.Samples * removed.Format.Channels * sizeof(float);
                    entry.Pitches.Remove(oldest);
                    entry.RecentPitches.RemoveFirst();
                }

                entry.Pitches.Add(pitch, (buffer, entry.RecentPitches.AddLast(pitch)));
                entry.PitchBytes += bytes;
                return buffer;
            }
        }
    }
}
