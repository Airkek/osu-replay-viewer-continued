using System;

namespace osu_replay_renderer_netcore.CustomHosts.Record;

public interface IEncoderAudioInput
{
    void ConfigureAudio(int sampleRate, int channels);
    void WriteAudio(ReadOnlySpan<byte> pcm);
}
