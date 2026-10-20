using System;

namespace osu_replay_renderer_netcore.Audio
{
    internal readonly record struct MusicPlaybackSegment(
        long OutputStartSample,
        long OutputEndSample,
        double SourceStartSeconds,
        double Frequency,
        double Tempo)
    {
        public static MusicPlaybackSegment FromClockInterval(double outputStartMs, double sourceStartMs,
            double durationMs, double frequency, double tempo, int sampleRate)
        {
            double rate = frequency * tempo;
            if (!double.IsFinite(outputStartMs) || outputStartMs < 0 || !double.IsFinite(sourceStartMs) ||
                !double.IsFinite(durationMs) || durationMs < 0 || !double.IsFinite(frequency) || frequency <= 0 ||
                !double.IsFinite(tempo) || tempo <= 0 || !double.IsFinite(rate) || rate <= 0 || sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(outputStartMs));

            long first = checked((long)Math.Round(outputStartMs / 1000 * sampleRate));
            long end = checked((long)Math.Round((outputStartMs + durationMs) / 1000 * sampleRate));
            double quantizedSourceStart = sourceStartMs / 1000 +
                (first / (double)sampleRate - outputStartMs / 1000) * rate;
            return new MusicPlaybackSegment(first, end, quantizedSourceStart, frequency, tempo);
        }
    }
}
