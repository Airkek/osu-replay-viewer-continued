using osu.Framework.Audio.Sample;
using osu.Framework.Audio.Track;
using osu.Framework.Graphics.Audio;
using System;
using System.Collections.Generic;
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

        public double LongestDuration
        {
            get
            {
                return JournalElements.Select(x => x.Time + x.Buffer.Duration).Max();
            }
        }

        public void SampleAt(double t, ISample sample, Func<AudioBuffer, AudioBuffer> process = null)
        {
            int recursionAllowed = 50;
            while (sample is DrawableSample sample2 && recursionAllowed > 0)
            {
                sample = sample2.GetUnderlaying();
                recursionAllowed--;
            }

            if (sample is SampleVirtual) return;
            if (recursionAllowed <= 0) throw new Exception($"Recursion exceed while getting SampleBass instance");
            if (!sample.IsSampleBass()) throw new Exception($"The given sample doesn't have SampleBass instance");

            var bass = sample.AsSampleBass();
            if (bass.SampleId == 0) return;

            AudioBuffer buff;
            if (!CachedSampleBuffers.TryGetValue(bass.SampleId, out var buffer))
            {
                buff = bass.AsAudioBuffer();
                if (buff == null) return;

                CachedSampleBuffers.Add(bass.SampleId, buff);
            }
            else buff = buffer;
            BufferAt(t, buff, process);
        }

        public void BufferAt(double t, AudioBuffer buff, Func<AudioBuffer, AudioBuffer> process = null)
        {
            if (process != null) buff = process(buff);
            JournalElements.Add(new JournalElement { Time = t, Buffer = buff });
        }

        public void MixSamples(AudioBuffer buffer)
        {
            SamplesMixer mixer = new(buffer);
            foreach (var element in JournalElements) mixer.Mix(element.Buffer, element.Time);
        }

        public void Reset()
        {
            JournalElements.Clear();
        }

        public struct JournalElement
        {
            public AudioBuffer Buffer;
            public double Time;
        }
    }
}
