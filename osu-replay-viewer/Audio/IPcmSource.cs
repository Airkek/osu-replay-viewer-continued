using System;

namespace osu_replay_renderer_netcore.Audio
{
    internal interface IPcmSource : IDisposable
    {
        AudioFormat Format { get; }

        // The exact frame count remains unknown until EOF is reached after valid decoding
        // long.MaxValue represents an unknown length
        long Length { get; }

        // Reads interleaved frames and pads negative positions or EOF with silence
        void ReadFrames(long firstFrame, Span<float> output);
    }
}
