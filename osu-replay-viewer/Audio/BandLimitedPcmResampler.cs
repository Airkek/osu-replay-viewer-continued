using System;
using System.Buffers;
using System.Collections.Concurrent;

namespace osu_replay_renderer_netcore.Audio
{
    internal static class BandLimitedPcmResampler
    {
        private const int taps = 64;
        private const int phases = 256;
        private const int cutoffSteps = 128;
        private const int maximumOutputFrames = 4096;
        private const int maximumSourceFrames = 16384;
        private static readonly ConcurrentDictionary<int, float[]> kernels = new();

        public static void Read(IPcmSource source, double position, double step, Span<float> output)
        {
            int channels = source.Format.Channels;
            if (channels <= 0 || output.Length % channels != 0 || !double.IsFinite(position) ||
                !double.IsFinite(step) || step <= 0)
                throw new ArgumentOutOfRangeException(nameof(step));

            output.Clear();
            int frames = output.Length / channels;
            if (frames == 0) return;

            double roundedPosition = Math.Round(position);
            if (step == 1 && Math.Abs(position - roundedPosition) < 1e-7)
            {
                if (roundedPosition <= -frames || roundedPosition >= source.Length || roundedPosition >= long.MaxValue)
                    return;
                source.ReadFrames((long)roundedPosition, output);
                return;
            }

            int cutoff = Math.Clamp((int)Math.Floor(cutoffSteps / Math.Max(1, step)), 1, cutoffSteps);
            var kernel = kernels.GetOrAdd(cutoff, CreateKernel);
            int firstOutputFrame = 0;
            if (position < 0)
            {
                double firstNonnegative = Math.Ceiling(-position / step);
                if (firstNonnegative >= frames) return;
                firstOutputFrame = (int)firstNonnegative;
            }

            var scratch = ArrayPool<float>.Shared.Rent(checked(maximumSourceFrames * channels));
            try
            {
                while (firstOutputFrame < frames)
                {
                    double firstAt = position + firstOutputFrame * step;
                    if (firstAt < 0)
                    {
                        firstOutputFrame++;
                        continue;
                    }
                    if (firstAt >= source.Length || firstAt >= long.MaxValue - taps)
                        break;

                    int count = (int)Math.Min(Math.Min(maximumOutputFrames, frames - firstOutputFrame),
                        Math.Floor((maximumSourceFrames - taps - 1) / step) + 1);
                    long firstCentre = (long)Math.Floor(firstAt);
                    double lastAt = position + (firstOutputFrame + count - 1) * step;
                    int sourceFrames = checked((int)((long)Math.Floor(lastAt) - firstCentre) + taps + 1);
                    long windowStart = firstCentre - taps / 2 + 1;
                    source.ReadFrames(windowStart, scratch.AsSpan(0, checked(sourceFrames * channels)));

                    long length = source.Length;
                    for (int frame = firstOutputFrame; frame < firstOutputFrame + count; frame++)
                    {
                        double at = position + frame * step;
                        if (at < 0 || at >= length) continue;
                        long centre = (long)Math.Floor(at);
                        int phase = (int)Math.Round((at - centre) * phases);
                        if (phase == phases)
                        {
                            centre++;
                            phase = 0;
                        }
                        int firstValue = checked((int)(centre - taps / 2 + 1 - windowStart) * channels);
                        int kernelOffset = phase * taps;
                        int outputOffset = frame * channels;
                        for (int tap = 0; tap < taps; tap++)
                        {
                            float weight = kernel[kernelOffset + tap];
                            int sourceOffset = firstValue + tap * channels;
                            for (int channel = 0; channel < channels; channel++)
                                output[outputOffset + channel] += scratch[sourceOffset + channel] * weight;
                        }
                    }
                    firstOutputFrame += count;
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(scratch);
            }
        }

        private static float[] CreateKernel(int cutoffStep)
        {
            var result = new float[phases * taps];
            double cutoff = cutoffStep == cutoffSteps ? 1 : .95 * cutoffStep / cutoffSteps;
            for (int phase = 0; phase < phases; phase++)
            {
                double fraction = phase / (double)phases;
                double sum = 0;
                for (int tap = 0; tap < taps; tap++)
                {
                    double distance = tap - taps / 2 + 1 - fraction;
                    double windowPosition = distance / (taps / 2);
                    double window = Math.Abs(windowPosition) >= 1 ? 0 :
                        .42 + .5 * Math.Cos(Math.PI * windowPosition) + .08 * Math.Cos(2 * Math.PI * windowPosition);
                    double scaledDistance = Math.PI * cutoff * distance;
                    double weight = cutoff * (Math.Abs(scaledDistance) < 1e-12 ? 1 : Math.Sin(scaledDistance) / scaledDistance) * window;
                    result[phase * taps + tap] = (float)weight;
                    sum += weight;
                }
                for (int tap = 0; tap < taps; tap++)
                    result[phase * taps + tap] /= (float)sum;
            }
            return result;
        }
    }
}
