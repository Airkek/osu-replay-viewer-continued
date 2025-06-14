using osu.Framework.Audio.Sample;
using osu.Framework.Audio.Track;
using osu.Framework.Graphics.Audio;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace osu_replay_renderer_netcore.Audio
{
    /// <summary>
    /// Samples journal: take notes of every single sample play events.
    /// Simply write the <see cref="ISample"/> using <see cref="SampleAt(double, ISample)"/>,
    /// then you can combine everything by calling <see cref="MixSamples(AudioBuffer)"/>.
    /// The mechanic is similar to digital audio workspace applications
    /// </summary>
    public class AudioJournal
    {
        public readonly List<JournalElement> JournalElements = new();
        public readonly Dictionary<int, AudioBuffer> CachedSampleBuffers = new();

        public delegate void SampleStopper(double endTime);

        public double LongestDuration
        {
            get
            {
                return JournalElements.Select(x => x.EndTime ?? x.Time + x.Buffer.Duration).Max();
            }
        }

        public SampleStopper SampleAt(double t, ISample sample, Func<AudioBuffer, AudioBuffer> process = null)
        {
            int recursionAllowed = 50;
            while (sample is DrawableSample sample2 && recursionAllowed > 0)
            {
                sample = sample2.GetUnderlaying();
                recursionAllowed--;
            }

            if (sample is SampleVirtual) return null;
            if (recursionAllowed <= 0) throw new Exception($"Recursion exceed while getting SampleBass instance");
            if (!sample.IsSampleBass()) throw new Exception($"The given sample doesn't have SampleBass instance");

            var bass = sample.AsSampleBass();
            if (bass.SampleId == 0) return null;

            AudioBuffer buff;
            if (!CachedSampleBuffers.TryGetValue(bass.SampleId, out var buffer))
            {
                buff = bass.AsAudioBuffer();
                if (buff == null) return null;

                CachedSampleBuffers.Add(bass.SampleId, buff);
            }
            else buff = buffer;
            return BufferAt(t, buff, process);
        }

        public SampleStopper BufferAt(double t, AudioBuffer buff, Func<AudioBuffer, AudioBuffer> process = null)
        {
            if (process != null) buff = process(buff);
            var element = new JournalElement { Time = t, Buffer = buff };
            JournalElements.Add(element);

            SampleStopper stopper = (endTime) =>
            {
                if (element.EndTime.HasValue)
                {
                    Debug.Assert(false);
                    return; 
                }
                element.EndTime = endTime;
            };
            return stopper;
        }

        public void MixSamples(AudioBuffer buffer)
        {
            SamplesMixer mixer = new(buffer);
            foreach (var element in JournalElements)
            {
                mixer.Mix(element.Buffer, element.Time, element.EndTime);
            }
        }

        public void Reset()
        {
            JournalElements.Clear();
        }

        public class JournalElement
        {
            public AudioBuffer Buffer;
            public double Time;
            public double? EndTime = null;
        }
    }
}
